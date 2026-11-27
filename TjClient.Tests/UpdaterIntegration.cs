using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using Chaos.NaCl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TjClient.Core;
using TjClient.Updates;
using TjClient.Wpf;

// Test assembly only. No production entrypoint, signing key, TLS or endpoint override.
internal static class UpdaterIntegration
{
    internal const string ConfigEnvironment = "TJCLIENT_UPDATER_E2E_CONFIG";
    internal const string OldVersion = "0.9.0";
    internal sealed record Input(string Root, string Setup, string InstallDirectory, string TargetVersion, bool Running, bool Tamper);
    internal sealed record Config(string Root, string InstallDirectory, string TargetVersion, string Address, string CaPem, string PublicKey, bool Running, bool Tamper);
    internal static void ValidateTargetVersion(string value)
    {
        Require(System.Text.RegularExpressions.Regex.IsMatch(value, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") &&
            System.Version.TryParse(value, out var parsed) && parsed.Major <= 65535 && parsed.Minor <= 65535 && parsed.Build <= 65535 &&
            parsed > System.Version.Parse(OldVersion), "NUMERIC_TARGET_VERSION_REQUIRED");
    }
    internal static string PackageUrl(string targetVersion)
    {
        ValidateTargetVersion(targetVersion);
        return $"https://github.com/PlotnikovDL/tjcloud-client/releases/download/v{targetVersion}/UpdaterIntegration-{targetVersion}-setup.exe";
    }
    public static int VersionContract(string value)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(new { targetVersion = value, packageUrl = PackageUrl(value) })); return 0; }
        catch { Console.Error.WriteLine("NUMERIC_TARGET_VERSION_REQUIRED"); return 1; }
    }
    internal sealed record Snapshot(string ProfileHash, string QueueHash, string QueueKey, long Offset, string? UploadId);
    internal static string Version => typeof(UpdaterIntegration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    internal static string FileAt(string root, string name) => Path.Combine(root, name);
    internal static void Require(bool value, string stage) { if (!value) throw new InvalidOperationException(stage); }
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void Write(string root, string name, object value)
    {
        var path = FileAt(root, name);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value));
        File.Move(temp, path, true);
    }
    internal static T Read<T>(string root, string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(FileAt(root, name)))!;
    internal static async Task Wait(string root, string name, int seconds = 60)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!File.Exists(FileAt(root, name))) await Task.Delay(50, deadline.Token);
    }
    internal static string Guard(string configPath)
    {
        Require(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted" &&
            Environment.GetEnvironmentVariable("RUNNER_OS") == "Windows" &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RUNNER_TEMP")), "HOSTED_WINDOWS_REQUIRED");
        var temporary = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(configPath);
        var root = Path.GetDirectoryName(path)!;
        Require(root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase), "OWNED_ROOT_REQUIRED");
        for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
            Require((ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "REPARSE_ROOT_REJECTED");
        Require(Guid.TryParseExact(File.ReadAllText(FileAt(root, ".tjclient-updater-e2e-owned")), "N", out _), "OWNERSHIP_MARKER_REQUIRED");
        if (File.Exists(path)) Require((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "REPARSE_CONFIG_REJECTED");
        return root;
    }
    internal static void OwnedChild(string root, string path)
    {
        Require(Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "OWNED_CHILD_REQUIRED");
        for (var ancestor = new DirectoryInfo(Path.GetDirectoryName(path)!); ancestor != null; ancestor = ancestor.Parent)
            if (ancestor.Exists) Require((ancestor.Attributes & FileAttributes.ReparsePoint) == 0, "REPARSE_CHILD_REJECTED");
    }
    internal static Snapshot State(string root)
    {
        var state = FileAt(root, "state");
        var settings = FileAt(state, "settings.dpapi");
        var profile = ProtectedStore.Load<ClientProfile>(settings)!;
        profile.RequireConsent();
        using var queue = new QueueStore(FileAt(state, profile.QueueKey + ".db"));
        queue.Bind(profile.Folder, profile.Endpoint, profile.SourceId);
        Require(queue.Initialized, "QUEUE_INITIALIZED");
        var rows = queue.All();
        Require(rows.Count == 1, "QUEUE_SINGLE_IDENTITY");
        return new(Hash(File.ReadAllBytes(settings)), Hash(JsonSerializer.SerializeToUtf8Bytes(rows)), profile.QueueKey, rows[0].Offset, rows[0].UploadId);
    }
    internal static void ProfileUnchanged(string root)
    {
        var original = ProtectedStore.Load<ClientProfile>(FileAt(root, "baseline.dpapi"))!;
        var current = ProtectedStore.Load<ClientProfile>(FileAt(root, "state/settings.dpapi"))!;
        Require(original == current && original.QueueKey == current.QueueKey && original.ConsentBinding == current.ConsentBinding &&
            original.Token == current.Token, "PROFILE_SEMANTICS_PRESERVED");
        Require(File.ReadAllBytes(FileAt(root, "baseline.dpapi")).SequenceEqual(File.ReadAllBytes(FileAt(root, "state/settings.dpapi"))), "PROFILE_BYTES_PRESERVED");
    }
    public static int GuardEntry(string path)
    {
        try { Guard(path); Console.WriteLine("GUARD_ALLOWED_HOSTED_ONLY"); return 0; }
        catch { Console.Error.WriteLine("HOSTED_WINDOWS_REQUIRED_OR_INVALID_OWNERSHIP"); return 1; }
    }
    public static async Task<int> Server(string inputPath)
    {
        string? root = null;
        try
        {
            root = Guard(inputPath);
            var input = JsonSerializer.Deserialize<Input>(File.ReadAllText(inputPath))!;
            ValidateTargetVersion(input.TargetVersion);
            Require(Path.GetFullPath(input.Root) == root, "INPUT_ROOT_MATCH");
            OwnedChild(root, input.InstallDirectory);
            Require(File.Exists(input.Setup) && Path.GetFullPath(input.Setup).StartsWith(Path.GetDirectoryName(root)! + "\\", StringComparison.OrdinalIgnoreCase), "OWNED_SETUP_REQUIRED");
            Require(FileVersionInfo.GetVersionInfo(input.Setup).ProductVersion?.Trim() == input.TargetVersion, "SETUP_PRODUCT_VERSION_MATCH");
            await using var server = await UpdaterIntegrationServer.Start(input);
            var config = new Config(root, input.InstallDirectory, input.TargetVersion, server.Address, server.Ca.ExportCertificatePem(), server.PublicKey, input.Running, input.Tamper);
            ProtectedStore.Save(FileAt(root, "client.dpapi"), config);
            var logs = FileAt(root, "logs");
            Directory.CreateDirectory(FileAt(logs, "rphost_1"));
            File.WriteAllText(FileAt(logs, "rphost_1/26112501.log"), "synthetic updater cross-process transfer\n");
            var state = FileAt(root, "state");
            var profile = new ClientProfile { Folder = logs, Endpoint = server.Address, Token = "synthetic-updater-fixture",
                SourceId = 1, Running = input.Running, IntervalSeconds = 60, Mode = InitialMode.ModifiedSince,
                Since = DateTimeOffset.MinValue }.Approve();
            ProtectedStore.Save(FileAt(state, "settings.dpapi"), profile);
            File.Copy(FileAt(state, "settings.dpapi"), FileAt(root, "baseline.dpapi"));
            using (var queue = new QueueStore(FileAt(state, profile.QueueKey + ".db")))
            {
                queue.Bind(profile.Folder, profile.Endpoint, profile.SourceId);
                queue.BeginObservation(profile.Folder, profile.Mode, profile.Since);
            }
            Write(root, "server-ready.json", new { pid = Environment.ProcessId, installerHash = Hash(File.ReadAllBytes(input.Setup)), version = input.TargetVersion });
            await Wait(root, "server-stop", 360);
            return 0;
        }
        catch (Exception error)
        {
            if (root != null) Write(root, "server-failure.json", new { stage = "SERVER", error = error.GetType().Name });
            Console.Error.WriteLine("UPDATER_E2E_SERVER_FAILED");
            return 1;
        }
    }
    public static Task<int> Client(string configPath)
    {
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string? root = null;
            try
            {
                root = Guard(configPath);
                var config = ProtectedStore.Load<Config>(configPath)!;
                ValidateTargetVersion(config.TargetVersion);
                Require(config.Root == root, "CONFIG_ROOT_MATCH");
                OwnedChild(root, config.InstallDirectory);
                Require(Path.GetFullPath(Environment.ProcessPath!) == Path.Combine(config.InstallDirectory, "TjCloudClient.exe"), "INSTALLED_TEST_BOOTSTRAP_REQUIRED");
                Require(Version == OldVersion || Version == config.TargetVersion, "ASSEMBLY_VERSION_REQUIRED");
                var isNew = Version == config.TargetVersion;
                using (var start = new FileStream(FileAt(root, isNew ? "new-start.json" : "old-start.json"), FileMode.CreateNew))
                    JsonSerializer.Serialize(start, new { pid = Environment.ProcessId, version = Version, utc = DateTimeOffset.UtcNow });
                ProfileUnchanged(root);
                if (isNew)
                {
                    Require(State(root) == Read<Snapshot>(root, "drained-state.json"), "PRESTARTUP_STATE_EQUAL");
                    Write(root, "new-prestartup.json", new { profileBytesEqual = true, queueIdentityAndOffsetEqual = true, semanticProfileEqual = true });
                }
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (_, e) => { e.Handled = true; Fail(root, "DISPATCHER", e.Exception); app.Shutdown(1); };
                using var ca = X509Certificate2.CreateFromPem(config.CaPem);
                using var collectorHttp = TestTls.Create(ca);
                using var updateHttp = new HttpClient(new Reroute(config, TestTls.CreateHandler(ca))) { Timeout = TimeSpan.FromSeconds(60) };
                var stage = "WINDOW";
                MainWindow? window = null;
                UpdateCoordinator? coordinator = null;
                var observed = new ObservedLifetime(root, () => window!);
                window = new MainWindow(FileAt(root, "state"), false,
                    p => new CollectorClient(p.Endpoint, p.Token, collectorHttp),
                    (directory, _) => coordinator = CreateCoordinator(directory, Version, config.PublicKey, observed, updateHttp),
                    async () =>
                    {
                        Require(!isNew, "CONSENT_MUST_NOT_REPEAT");
                        await Wait(root, "active-transfer.json");
                        window!.UpdaterAssertActive();
                        Require(State(root).Offset == 4, "ACK_OFFSET_BEFORE_UPDATE");
                        Write(root, "consent-once.json", new { count = 1, activeOperation = true, offset = 4 });
                        return true;
                    }) { ShowActivated = false };
                app.MainWindow = window;
                window.Loaded += async (_, _) =>
                {
                    try
                    {
                        stage = isNew ? "RESTART" : "OLD_TRANSFER";
                        // Paused intent can still have an explicit one-shot send in flight.
                        if (!isNew && !config.Running) _ = window.UpdaterOneShot();
                        if (isNew)
                        {
                            var before = Read<Snapshot>(root, "drained-state.json");
                            if (config.Running)
                            {
                                await Wait(root, "accepted.json");
                                await window.UpdaterWaitIdle();
                                using var queue = new QueueStore(FileAt(root, "state/" + before.QueueKey + ".db"));
                                var item = queue.All().Single();
                                Require(item.UploadId == before.UploadId && item.Offset == item.File.Size && item.State is QueueState.Delivered or QueueState.Imported, "RESUMED_SAME_UPLOAD");
                                // Проверяем следующий опрос после принятия передачи.
                                await window.UpdaterOneShot();
                            }
                            else
                            {
                                await Task.Delay(TimeSpan.FromSeconds(7));
                                Require(State(root) == before, "PAUSED_QUEUE_UNCHANGED");
                            }
                            window.UpdaterAssertIntent(config.Running);
                            ProfileUnchanged(root);
                            Require(coordinator!.Consent == true, "CONSENT_PERSISTED");
                            var stats = Read<UpdaterIntegrationServer.Statistics>(root, "collector-stats.json");
                            Require(stats.Registrations == 1 && stats.Completions == (config.Running ? 1 : 0), "NO_DUPLICATE_ACCEPTED_UPLOAD");
                            Require(stats.CancelledStalls == 1, "OLD_REQUEST_CANCELLED");
                            Require(stats.Bytes == (config.Running ? stats.Size : 4), "SERVER_OFFSET_MATCH");
                            Write(root, "new-pass.json", new { status = "PASS", pid = Environment.ProcessId, version = Version,
                                running = config.Running, stats.Registrations, stats.Completions, stats.Bytes, profileUnchanged = true,
                                queueIdentityPreserved = true, consentReused = true });
                        }
                        else if (config.Tamper)
                        {
                            await Wait(root, "consent-once.json");
                            await window.UpdaterWaitCheck();
                            // The automatic UI check is silent. Observe a second real check through the same coordinator.
                            var lastOutcome = await coordinator!.CheckAsync();
                            Require(observed.Prepared == 0 && observed.Closed == 0, "TAMPER_NO_PREPARE_OR_CLOSE");
                            Require(lastOutcome == UpdateOutcome.Failed, "TAMPER_FAILURE_OUTCOME");
                            await Wait(root, "tampered-download.json");
                            using var download = JsonDocument.Parse(File.ReadAllText(FileAt(root, "tampered-download.json")));
                            Require(download.RootElement.GetProperty("bytes").GetInt64() > 0 &&
                                download.RootElement.GetProperty("signedHash").GetString() != download.RootElement.GetProperty("servedHash").GetString(), "TAMPER_SIGNED_PACKAGE_SERVED");
                            ProfileUnchanged(root);
                            Require(!Directory.GetFiles(FileAt(root, "state/updates"), "*.exe").Any(), "TAMPER_DOWNLOAD_REMOVED");
                            Write(root, "tampered-pass.json", new { status = "TAMPER_REJECTED", pid = Environment.ProcessId, version = Version });
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMinutes(3));
                            throw new InvalidOperationException("OLD_DID_NOT_HAND_OFF");
                        }
                        await Wait(root, "client-stop", 180);
                        Require(await ((IUpdateLifetime)window).PrepareAsync(CancellationToken.None), "FINAL_DRAIN");
                        await ((IUpdateLifetime)window).CloseAsync();
                    }
                    catch (Exception error) { Fail(root, stage, error); app.Shutdown(1); }
                };
                window.Show();
                var exitCode = app.Run();
                result.TrySetResult(exitCode);
            }
            catch (Exception error)
            {
                if (root != null) Fail(root, "BOOTSTRAP", error);
                Console.Error.WriteLine("UPDATER_E2E_CLIENT_FAILED");
                result.TrySetResult(1);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
    private static void Fail(string root, string stage, Exception error) => Write(root, "client-failure.json", new { stage, error = error.GetType().Name, pid = Environment.ProcessId, version = Version });
    private sealed class Reroute(Config config, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Require(uri.AbsoluteUri == SignedUpdateBackend.FeedUrl || uri.AbsoluteUri == SignedUpdateBackend.FeedUrl + ".signature" ||
                uri.AbsoluteUri == PackageUrl(config.TargetVersion), "FIXED_SOURCE_ONLY");
            request.RequestUri = new Uri(config.Address + uri.AbsolutePath);
            return base.SendAsync(request, token);
        }
    }
    internal static UpdateCoordinator CreateCoordinator(string directory, string version, string key, IUpdateLifetime lifetime,
        HttpClient http) => new UpdateCoordinator(directory, () =>
            new SignedUpdateBackend(directory, version, key, lifetime, http, launch: null));
    private sealed class ObservedLifetime(string root, Func<MainWindow> getWindow) : IUpdateLifetime
    {
        public int Prepared, Closed;
        public async Task<bool> PrepareAsync(CancellationToken token)
        {
            Prepared++;
            getWindow().UpdaterAssertActive();
            var result = await ((IUpdateLifetime)getWindow()).PrepareAsync(token);
            Require(result, "REAL_SAFE_STOP_SUCCEEDED");
            getWindow().UpdaterAssertDrained();
            ProfileUnchanged(root);
            var snapshot = State(root);
            Require(snapshot.Offset == 4 && snapshot.UploadId != null, "DRAINED_ACK_OFFSET");
            Write(root, "drained-state.json", snapshot);
            Write(root, "old-drained.json", new { pid = Environment.ProcessId, version = Version, prepared = Prepared, activeOperations = 0, busy = false, offset = snapshot.Offset });
            return result;
        }
        public Task ResumeAsync() => ((IUpdateLifetime)getWindow()).ResumeAsync();
        public Task LaunchAsync(Action launch, CancellationToken token) => ((IUpdateLifetime)getWindow()).LaunchAsync(launch, token);
        public Task CloseAsync() => CloseAsync(() => { }, CancellationToken.None);
        public async Task CloseAsync(Action commit, CancellationToken token)
        {
            Require(++Closed == 1 && Prepared == 1, "SINGLE_HANDOFF");
            getWindow().UpdaterAssertDrained();
            // Hold the drained old process alive until the observer sees the real installer waiting.
            Write(root, "old-close-enter.json", new { pid = Environment.ProcessId, version = Version });
            await Wait(root, "installer-observed.json", 30);
            using var observation = JsonDocument.Parse(File.ReadAllText(FileAt(root, "installer-observed.json")));
            Require(observation.RootElement.GetProperty("oldPid").GetInt32() == Environment.ProcessId &&
                observation.RootElement.GetProperty("oldStartFileTime").GetString() == Process.GetCurrentProcess().StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                observation.RootElement.GetProperty("readyObserved").GetBoolean() &&
                observation.RootElement.GetProperty("oldStillAlive").GetBoolean(), "INSTALLER_WAIT_OBSERVED");
            Write(root, "old-close.json", new { pid = Environment.ProcessId, version = Version, closeCount = Closed });
            await ((IUpdateLifetime)getWindow()).CloseAsync(commit, token);
        }
    }
}

internal sealed class UpdaterIntegrationServer : IAsyncDisposable
{
    internal sealed record Statistics(int Registrations, int Completions, long Bytes, long Size, int CancelledStalls);
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    private readonly UpdaterIntegration.Input input;
    private readonly MemoryCollector collector = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly byte[] payload, feed, signature;
    private readonly string signedPayloadHash;
    private int stalled, cancelled;
    public X509Certificate2 Ca { get; }
    public string PublicKey { get; }
    public string Address { get; private set; } = "";
    private UpdaterIntegrationServer(WebApplication app, X509Certificate2 ca, X509Certificate2 certificate, UpdaterIntegration.Input input)
    {
        this.app = app; Ca = ca; this.certificate = certificate; this.input = input;
        payload = File.ReadAllBytes(input.Setup);
        signedPayloadHash = UpdaterIntegration.Hash(payload);
        var seed = RandomNumberGenerator.GetBytes(32);
        try
        {
            var publicKey = Ed25519.PublicKeyFromSeed(seed);
            PublicKey = Convert.ToBase64String(publicKey);
            string Sign(byte[] bytes)
            {
                var signer = new Ed25519Signer();
                signer.InitWithNonExpandedPrivateKey(publicKey, seed);
                signer.AddToBuffer(bytes, 0, bytes.Length);
                return Convert.ToBase64String(signer.GenerateSignature());
            }
            feed = Encoding.UTF8.GetBytes($"<rss version=\"2.0\" xmlns:sparkle=\"http://www.andymatuschak.org/xml-namespaces/sparkle\"><channel><item><sparkle:channel>stable</sparkle:channel><enclosure url=\"{UpdaterIntegration.PackageUrl(input.TargetVersion)}\" length=\"{payload.Length}\" sparkle:version=\"{input.TargetVersion}\" sparkle:os=\"windows-x64\" sparkle:signature=\"{Sign(payload)}\" /></item></channel></rss>");
            signature = Encoding.UTF8.GetBytes(Sign(feed));
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
        if (input.Tamper) payload[^1] ^= 1;
    }
    public static async Task<UpdaterIntegrationServer> Start(UpdaterIntegration.Input input)
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=TJ Updater Integration CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var ca = X509Certificate2.CreateFromPem(root.ExportCertificatePem());
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var server = new UpdaterIntegrationServer(app, ca, certificate, input);
        app.Run(server.Handle);
        await app.StartAsync();
        server.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost");
        return server;
    }
    private void Stats() => UpdaterIntegration.Write(input.Root, "collector-stats.json", new Statistics(collector.Registrations, collector.Completions, collector.Bytes.Length, collector.Size, Volatile.Read(ref cancelled)));
    private async Task Handle(HttpContext context)
    {
        var path = context.Request.Path.Value!;
        if (path == new Uri(SignedUpdateBackend.FeedUrl).AbsolutePath || path == new Uri(SignedUpdateBackend.FeedUrl + ".signature").AbsolutePath || path == new Uri(UpdaterIntegration.PackageUrl(input.TargetVersion)).AbsolutePath)
        {
            var bytes = path.EndsWith(".signature", StringComparison.Ordinal) ? signature : path.EndsWith(".xml", StringComparison.Ordinal) ? feed : payload;
            context.Response.ContentLength = bytes.Length;
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            if (input.Tamper && path == new Uri(UpdaterIntegration.PackageUrl(input.TargetVersion)).AbsolutePath)
                UpdaterIntegration.Write(input.Root, "tampered-download.json", new { bytes = bytes.Length, signedHash = signedPayloadHash, servedHash = UpdaterIntegration.Hash(bytes) });
            return;
        }
        if (!path.StartsWith("/api/collector/v1/", StringComparison.Ordinal)) { context.Response.StatusCode = 404; return; }
        // Stall the second chunk before accepting it. The first ACK is already durable in SQLite.
        if (path.EndsWith("/chunk", StringComparison.Ordinal) && context.Request.Query["offset"] == "4" && Interlocked.CompareExchange(ref stalled, 1, 0) == 0)
        {
            UpdaterIntegration.Write(input.Root, "active-transfer.json", new { acknowledgedOffset = 4, serverPid = Environment.ProcessId });
            try { await Task.Delay(TimeSpan.FromMinutes(3), context.RequestAborted); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); Stats(); }
            context.Abort();
            return;
        }
        await serial.WaitAsync(context.RequestAborted);
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), "https://localhost" + path + context.Request.QueryString)
                { Content = new StreamContent(context.Request.Body) };
            using var response = await collector.Handle(message);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            await response.Content.CopyToAsync(context.Response.Body);
            Stats();
            if (collector.Completions == 1) UpdaterIntegration.Write(input.Root, "accepted.json", new { completions = 1 });
        }
        finally { serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        certificate.Dispose(); Ca.Dispose(); serial.Dispose(); collector.Bytes.Dispose();
    }
}
