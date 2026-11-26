using System.Net.Http;
using System.Diagnostics;
using System.Globalization;

namespace TjClient.Updates;

public interface IUpdateLifetime
{
    Task<bool> PrepareAsync(CancellationToken token);
    Task ResumeAsync();
    Task CloseAsync();
    Task LaunchAsync(Action launch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        launch();
        return Task.CompletedTask;
    }
    async Task CloseAsync(Action commit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        commit();
        await CloseAsync();
    }
}

public sealed class SignedUpdateBackend : IUpdateBackend
{
    public const string FeedUrl = "https://github.com/PlotnikovDL/tjcloud-client/releases/latest/download/appcast.xml";
    public const string InstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /RESTARTCLIENT=1";
    private readonly string directory, installedVersion, publicKey;
    private readonly IUpdateLifetime lifetime;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Func<string, Task>? testLaunch;
    private readonly SemaphoreSlim serial = new(1, 1);
    private bool handedOff;
    private bool handoffComplete;
    private InstallerHandoff? pendingHandoff;
    internal Action<Action, CancellationToken>? AuthorizeHandoff { get; set; }
    public SignedUpdateBackend(string directory, string version, string key, IUpdateLifetime lifetime)
        : this(directory, version, key, lifetime, new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false })
            { Timeout = TimeSpan.FromMinutes(10) }, null, true) { }
    internal SignedUpdateBackend(string directory, string version, string key, IUpdateLifetime lifetime,
        HttpClient http, Func<string, Task>? launch, bool ownsHttp = false)
    {
        this.directory = Path.Combine(Path.GetFullPath(directory), "updates");
        installedVersion = version;
        publicKey = key.Trim();
        if (Convert.FromBase64String(publicKey).Length != 32 || !Checker().HasValidKeyInformation())
            throw new InvalidDataException("Некорректный ключ проверки обновлений.");
        this.lifetime = lifetime;
        this.http = http;
        this.ownsHttp = ownsHttp;
        testLaunch = launch;
    }
    private NetSparkleUpdater.SignatureVerifiers.Ed25519Checker Checker() => new(
        NetSparkleUpdater.Enums.SecurityMode.Strict, publicKey, publicKeyFile: null, readFileBeingVerifiedInChunks: true);

    public async Task<UpdateOutcome> CheckAsync(CancellationToken token)
    {
        await serial.WaitAsync(token);
        var prepared = false;
        var prepareAttempted = false;
        var closed = false;
        string? path = null;
        try
        {
            if (handedOff) return handoffComplete ? UpdateOutcome.InstallerLaunched : UpdateOutcome.Deferred;
            var feed = await ReadBytes(new Uri(FeedUrl), 1024 * 1024, token);
            var signature = System.Text.Encoding.UTF8.GetString(await ReadBytes(new Uri(FeedUrl + ".signature"), 256, token)).Trim();
            if (Checker().VerifySignature(signature, feed) != NetSparkleUpdater.Enums.ValidationResult.Valid)
                return UpdateOutcome.Failed;
            var item = SelectUpdate(feed);
            if (item == null) return UpdateOutcome.UpToDate;
            Directory.CreateDirectory(directory);
            using var sparkle = new ApplicationSparkle(FeedUrl, Checker(), async installerPath =>
            {
                token.ThrowIfCancellationRequested();
                if (!prepared) throw new InvalidOperationException("Нет разрешения на выход для обновления.");
                await HandoffAsync(installerPath, token);
                closed = true;
                handoffComplete = true;
            })
            {
                Configuration = new NetSparkleUpdater.Configurations.DefaultConfiguration(
                    new NetSparkleUpdater.AssemblyAccessors.AssemblyDiagnosticsAccessor(typeof(SignedUpdateBackend).Assembly.Location)),
                UserInteractionMode = NetSparkleUpdater.Enums.UserInteractionMode.DownloadNoInstall,
                RelaunchAfterUpdate = false,
                CustomInstallerArguments = InstallerArguments,
                CheckServerFileName = false,
                TmpDownloadFilePath = directory,
                TmpDownloadFileNameWithExtension = "update-" + Guid.NewGuid().ToString("N") + ".exe",
                LogWriter = null
            };
            var downloaded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Synchronous event only queues data; installation is awaited below on this serialized task.
            sparkle.DownloadFinished += (_, file) => downloaded.TrySetResult(file);
            sparkle.DownloadHadError += (_, _, _) => downloaded.TrySetException(new IOException("Ошибка загрузки обновления."));
            sparkle.DownloadedFileIsCorrupt += (_, _) => downloaded.TrySetException(new InvalidDataException("Подпись обновления не совпадает."));
            var installFailed = false;
            sparkle.InstallUpdateFailed += (_, _) => { installFailed = true; return false; };
            sparkle.PreparingToExitAsync += async (_, e) =>
            {
                e.Cancel = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    prepareAttempted = true;
                    prepared = await lifetime.PrepareAsync(token);
                    e.Cancel = !prepared || token.IsCancellationRequested;
                }
                catch { e.Cancel = true; }
            };
            path = await sparkle.GetDownloadPathForAppCastItem(item) ?? throw new IOException();
            await DownloadFile(new Uri(item.DownloadLink!), path, item.UpdateSize, token);
            // InitAndBeginDownload verifies our fully downloaded file and raises DownloadFinished.
            // No async-void download callback or library-owned background task is left unobserved.
            if (Checker().VerifySignatureOfFile(item.DownloadSignature!, path) != NetSparkleUpdater.Enums.ValidationResult.Valid)
                return UpdateOutcome.Failed;
            await sparkle.InitAndBeginDownload(item);
            var verified = await downloaded.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            token.ThrowIfCancellationRequested();
            await sparkle.InstallUpdate(item, verified); // fresh signature verification AFTER PreparingToExitAsync
            if (closed) return UpdateOutcome.InstallerLaunched;
            return installFailed ? UpdateOutcome.Failed : UpdateOutcome.Deferred;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return UpdateOutcome.Cancelled; }
        catch { return UpdateOutcome.Failed; }
        finally
        {
            try
            {
                if (!closed && !handedOff)
                {
                    if (prepareAttempted) await lifetime.ResumeAsync();
                    if (path != null) { try { File.Delete(path); } catch (IOException) { } }
                }
            }
            finally { serial.Release(); }
        }
    }

    private NetSparkleUpdater.AppCastItem? SelectUpdate(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
            { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = System.Xml.Linq.XDocument.Load(reader);
        System.Xml.Linq.XNamespace ns = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        var channel = document.Root?.Element("channel") ?? throw new InvalidDataException();
        return channel.Elements("item").Select(element =>
        {
            var enclosure = element.Element("enclosure") ?? throw new InvalidDataException();
            return new NetSparkleUpdater.AppCastItem
            {
                Version = (string?)enclosure.Attribute(ns + "version"),
                OperatingSystem = (string?)enclosure.Attribute(ns + "os"),
                Channel = (string?)element.Element(ns + "channel") ?? "stable",
                DownloadLink = (string?)enclosure.Attribute("url"),
                DownloadSignature = (string?)enclosure.Attribute(ns + "signature") ?? (string?)enclosure.Attribute(ns + "edSignature"),
                UpdateSize = (long?)enclosure.Attribute("length") ?? 0
            };
        }).Where(ValidateItem).Where(item => Version.Parse(item.Version!) > Version.Parse(installedVersion))
          .OrderByDescending(item => Version.Parse(item.Version!)).FirstOrDefault();
    }

    private static bool ValidateItem(NetSparkleUpdater.AppCastItem item)
    {
        if (item.OperatingSystem != "windows-x64" || item.Channel is not ("stable" or "release" or "") ||
            !System.Text.RegularExpressions.Regex.IsMatch(item.Version ?? "", @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$") ||
            !Version.TryParse(item.Version, out _) || string.IsNullOrWhiteSpace(item.DownloadSignature) ||
            Convert.FromBase64String(item.DownloadSignature).Length != 64 ||
            !Uri.TryCreate(item.DownloadLink, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !url.AbsolutePath.StartsWith("/PlotnikovDL/tjcloud-client/releases/download/v" + item.Version + "/", StringComparison.Ordinal) ||
            !System.Text.RegularExpressions.Regex.IsMatch(url.Segments.Last(), @"^[A-Za-z0-9._-]+\.exe$") ||
            item.UpdateSize <= 0 || item.UpdateSize > 512L * 1024 * 1024)
            throw new InvalidDataException("Обновление не соответствует стабильному каналу Windows x64.");
        return true;
    }

    private async Task<byte[]> ReadBytes(Uri uri, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        await Copy(uri, output, maximum, token);
        return output.ToArray();
    }
    private async Task DownloadFile(Uri uri, string path, long size, CancellationToken token)
    {
        if (size <= 0 || size > 512L * 1024 * 1024) throw new InvalidDataException();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await Copy(uri, output, size, token);
        if (output.Length != size) throw new InvalidDataException();
        await output.FlushAsync(token);
    }
    private async Task Copy(Uri uri, Stream output, long maximum, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException();
            await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
        }
    }
    public void Dispose() { if (ownsHttp) http.Dispose(); }

    private void Authorized(Action action, CancellationToken token)
    {
        if (AuthorizeHandoff != null) AuthorizeHandoff(action, token);
        else { token.ThrowIfCancellationRequested(); action(); }
    }

    private async Task HandoffAsync(string path, CancellationToken token)
    {
        if (testLaunch != null)
        {
            Task launch = Task.CompletedTask;
            await lifetime.LaunchAsync(() => Authorized(() => launch = testLaunch(path), token), token);
            await launch;
            // A callback does not provide an abort acknowledgement. Keep ownership on close failure.
            handedOff = true;
            await lifetime.CloseAsync(() => Authorized(() => { }, token), token);
            return;
        }
        var handoff = new InstallerHandoff();
        pendingHandoff = handoff;
        using var cancelled = token.Register(handoff.Cancel);
        try
        {
            var start = handoff.StartInfo(path);
            await lifetime.LaunchAsync(() => Authorized(() =>
            {
                // No await or shell between the final authorization gate and process creation.
                handoff.Start(start);
                handedOff = true;
            }, token), token);
            await handoff.WaitReadyAsync(token);
            await lifetime.CloseAsync(() => Authorized(handoff.Commit, token), token);
        }
        catch
        {
            cancelled.Dispose();
            if (!handedOff || await handoff.AbortAsync())
            {
                handedOff = false;
                handoff.Dispose();
                pendingHandoff = null;
            }
            // If acknowledgement is missing, retain signalled abort handles and stay quiescent.
            throw;
        }
    }

    internal sealed class InstallerHandoff : IDisposable
    {
        private readonly EventWaitHandle ready, abort, stopped;
        private readonly Process parent = Process.GetCurrentProcess();
        private Process? installer;
        private volatile bool committed;
        internal string Identity { get; }
        internal InstallerHandoff()
        {
            Identity = parent.Id.ToString(CultureInfo.InvariantCulture) + "." +
                parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            ready = Create("Ready");
            try { abort = Create("Abort"); }
            catch { ready.Dispose(); parent.Dispose(); throw; }
            try { stopped = Create("Stopped"); }
            catch { ready.Dispose(); abort.Dispose(); parent.Dispose(); throw; }
        }
        private EventWaitHandle Create(string suffix)
        {
            var handle = new EventWaitHandle(false, EventResetMode.ManualReset,
                @"Local\TJCloudClient.Update." + Identity + "." + suffix, out var created);
            if (created) return handle;
            handle.Dispose();
            throw new IOException("Предыдущая передача установщику ещё не завершена.");
        }
        internal ProcessStartInfo StartInfo(string path)
        {
            var start = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false };
            foreach (var argument in InstallerArguments.Split(' ')) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("/WAITPID=" + parent.Id.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("/WAITSTART=" + parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture));
            return start;
        }
        internal void Start(ProcessStartInfo start) => installer = Process.Start(start) ?? throw new IOException("Установщик не запущен.");
        internal async Task WaitReadyAsync(CancellationToken token)
        {
            var wait = Stopwatch.StartNew();
            while (!ready.WaitOne(0))
            {
                token.ThrowIfCancellationRequested();
                if (stopped.WaitOne(0) || wait.Elapsed > TimeSpan.FromSeconds(30))
                    throw new IOException("Установщик не подтвердил безопасное ожидание.");
                await Task.Delay(25, token);
            }
            token.ThrowIfCancellationRequested();
            if (stopped.WaitOne(0)) throw new IOException("Установщик отказался от обновления.");
        }
        internal void Commit()
        {
            if (stopped.WaitOne(0)) throw new IOException("Установщик отказался от обновления.");
            committed = true;
        }
        internal void Cancel() { if (!committed) abort.Set(); }
        internal async Task<bool> AbortAsync()
        {
            abort.Set();
            var wait = Stopwatch.StartNew();
            while (!stopped.WaitOne(0))
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(5)) return false;
                await Task.Delay(25);
            }
            return true;
        }
        public void Dispose()
        {
            ready.Dispose(); abort.Dispose(); stopped.Dispose(); installer?.Dispose(); parent.Dispose();
        }
    }

    private sealed class ApplicationSparkle : NetSparkleUpdater.SparkleUpdater
    {
        private readonly Func<string, Task> handoff;
        public ApplicationSparkle(string feed, NetSparkleUpdater.Interfaces.ISignatureVerifier checker,
            Func<string, Task> handoff) : base(feed, checker)
        {
            this.handoff = handoff;
        }
        protected override Task RunDownloadedInstaller(string path) => handoff(path);
    }
}
