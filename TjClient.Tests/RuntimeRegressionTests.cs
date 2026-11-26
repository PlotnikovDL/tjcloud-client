using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TjClient.Updates;

static class RuntimeRegressionTests
{
    public static void ShellFreeArguments() => ExerciseHandoff(null);
    public static void TerminalBeforeReady() => ExerciseHandoff("before-ready");
    public static void TerminalBeforeCommit() => ExerciseHandoff("before-commit");

    private static void ExerciseHandoff(string? terminalBoundary) => ExerciseHandoffAsync(terminalBoundary).GetAwaiter().GetResult();

    // No WPF is exercised here. The compiler and child each own their bounded wait.
    private static async Task ExerciseHandoffAsync(string? terminalBoundary)
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "source");
        var output = Path.Combine(fixture.Root, "space !TJ_RUNTIME_UNSET! %TJ_RUNTIME_UNSET% & path");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Harmless.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><UseAppHost>true</UseAppHost></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(source, "Program.cs"), """
            using System.Diagnostics;
            using System.Text.Json;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "child.tmp"), Environment.ProcessId.ToString());
            File.Move(Path.Combine(AppContext.BaseDirectory, "child.tmp"), Path.Combine(AppContext.BaseDirectory, "child.txt"));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "argv.json"), JsonSerializer.Serialize(args));
            var pid = args.Single(x => x.StartsWith("/WAITPID="))[9..];
            var start = args.Single(x => x.StartsWith("/WAITSTART="))[11..];
            var prefix = @"Local\TJCloudClient.Update." + pid + "." + start + ".";
            using var ready = EventWaitHandle.OpenExisting(prefix + "Ready");
            using var abort = EventWaitHandle.OpenExisting(prefix + "Abort");
            using var stopped = EventWaitHandle.OpenExisting(prefix + "Stopped");
            using var parent = Process.GetProcessById(int.Parse(pid));
            if (parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() != start) return 2;
            ready.Set();
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, "terminal.txt")))
            {
                var wait = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(AppContext.BaseDirectory, "reject-now.txt")))
                {
                    if (wait.Elapsed > TimeSpan.FromSeconds(10)) return 4;
                    Thread.Sleep(10);
                }
                stopped.Set();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "stopped.txt"), "terminal rejection, never copy files");
                return 0;
            }
            if (!abort.WaitOne(TimeSpan.FromSeconds(20))) return 3;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "aborted.txt"), "no installation, no files copied");
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "noack.txt"))) stopped.Set();
            return 0;
            """);
        var build = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = source };
        foreach (var arg in new[] { "publish", Path.Combine(source, "Harmless.csproj"), "-c", "Release", "-o", output })
            build.ArgumentList.Add(arg);
        build.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        build.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        build.Environment["DOTNET_CLI_HOME"] = Path.Combine(fixture.Root, "dotnet-home");
        build.Environment["NUGET_PACKAGES"] = Path.Combine(fixture.Root, "packages");
        using (var compiler = Process.Start(build)!)
        {
            var stdout = compiler.StandardOutput.ReadToEndAsync();
            var stderr = compiler.StandardError.ReadToEndAsync();
            await compiler.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            var diagnostic = await stdout + await stderr;
            Assert.True(compiler.ExitCode == 0, "Harmless argv fixture did not build: " + diagnostic);
        }
        var executable = Path.Combine(output, "Harmless.exe");
        if (terminalBoundary != null)
        {
            var state = Path.Combine(fixture.Root, terminalBoundary);
            var download = Path.Combine(state, "updates");
            using var feed = new SignedUpdateTests.Feed { Payload = File.ReadAllBytes(executable) };
            using var http = new HttpClient(feed);
            using var lifetime = new TerminalLifetime(output, download, terminalBoundary);
            using var backend = new SignedUpdateBackend(state, "0.9.0", feed.PublicKey, lifetime, http, null);
            await using var coordinator = new UpdateCoordinator(state, () => backend);
            await coordinator.SetConsentAsync(true);
            try
            {
            var outcome = await coordinator.CheckAsync();
            Console.WriteLine($"TERMINAL {terminalBoundary} outcome={outcome} launches={lifetime.Launches} closeAttempts={lifetime.CloseAttempts} closed={lifetime.Closed} resumed={lifetime.Resumed}");
            Assert.True(ReferenceEquals(backend.AuthorizeHandoff?.Target, coordinator), "Production authorization must be connected");
            Assert.Equal(UpdateOutcome.Failed, outcome);
            Assert.Equal(1, lifetime.Launches);
            Assert.Equal(terminalBoundary == "before-ready" ? 0 : 1, lifetime.CloseAttempts);
            Assert.Equal(0, lifetime.Closed);
            Assert.Equal(1, lifetime.Resumed);
            Assert.True(!Directory.EnumerateFiles(download, "update-*.exe").Any(), "Failed handoff payload cleanup did not finish");
            }
            finally
            {
                // A failing RED may leave a committed test handoff, although this harmless child has exited.
                var pending = typeof(SignedUpdateBackend).GetField("pendingHandoff", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                (pending!.GetValue(backend) as IDisposable)?.Dispose();
            }
            return;
        }
        using (var handoff = new SignedUpdateBackend.InstallerHandoff())
        {
        var startInfo = handoff.StartInfo(executable);
        Assert.True(!startInfo.UseShellExecute && startInfo.FileName == executable);
        Assert.Equal("", startInfo.Arguments);
        handoff.Start(startInfo);
        await handoff.WaitReadyAsync(CancellationToken.None);
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(output, "child.txt"))));
        _ = child.Handle;
        var actual = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(output, "argv.json")))!;
        Assert.True(actual.SequenceEqual(startInfo.ArgumentList), "Actual harmless program argv changed in transit");
        Assert.True(actual.Take(SignedUpdateBackend.InstallerArguments.Split(' ').Length).SequenceEqual(SignedUpdateBackend.InstallerArguments.Split(' ')));
        Assert.True(await handoff.AbortAsync());
        Assert.True(File.Exists(Path.Combine(output, "aborted.txt")));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, child.ExitCode);
        }
        Console.WriteLine("HARMLESS_ARGV preserved spaces ! % &; abort acknowledgement observed");
        foreach (var mode in new[] { "close-failure", "late-cancel", "no-ack" })
        {
            var state = Path.Combine(fixture.Root, mode + " !TJ_RUNTIME_UNSET! %TJ_RUNTIME_UNSET% & path");
            var download = Path.Combine(state, "updates");
            using var feed = new SignedUpdateTests.Feed { Payload = File.ReadAllBytes(executable) };
            using var http = new HttpClient(feed);
            using var cancel = new CancellationTokenSource();
            var lifetime = new FaultLifetime(output, download, mode, cancel);
            using var backend = new SignedUpdateBackend(state, "0.9.0", feed.PublicKey, lifetime, http, null);
            Assert.Equal(mode == "late-cancel" ? UpdateOutcome.Cancelled : UpdateOutcome.Failed, await backend.CheckAsync(cancel.Token));
            Assert.Equal(mode == "no-ack" ? 0 : 1, lifetime.Resumed);
            if (mode == "no-ack")
            {
                var requests = feed.Requests;
                Assert.Equal(UpdateOutcome.Deferred, await backend.CheckAsync(CancellationToken.None));
                Assert.Equal(requests, feed.Requests);
            }
            Assert.True(lifetime.Child != null);
            await lifetime.Child!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, lifetime.Child.ExitCode);
            lifetime.Child.Dispose();
            Console.WriteLine("HARMLESS_BACKEND " + mode + (mode == "no-ack" ? " no Resume or second launch without acknowledgement" : " acknowledged abort before Resume"));
        }
    }

    private sealed class TerminalLifetime(string helper, string download, string boundary) : IUpdateLifetime, IDisposable
    {
        public int Closed, Resumed, CloseAttempts, Launches;
        private Process? child;
        private bool terminalConfirmed;
        public Task<bool> PrepareAsync(CancellationToken token)
        {
            foreach (var file in Directory.GetFiles(helper).Where(x => x.EndsWith(".dll") || x.EndsWith(".runtimeconfig.json") || x.EndsWith(".deps.json")))
                File.Copy(file, Path.Combine(download, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(download, "terminal.txt"), "synthetic terminal rejection");
            return Task.FromResult(true);
        }
        private async Task WaitForFile(string name)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(Path.Combine(download, name))) await Task.Delay(10, deadline.Token);
        }
        public async Task LaunchAsync(Action launch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            launch();
            Launches++;
            await WaitForFile("child.txt");
            child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(download, "child.txt"))));
            _ = child.Handle;
            if (boundary == "before-ready") await Reject();
        }
        private async Task Reject()
        {
            File.WriteAllText(Path.Combine(download, "reject-now.txt"), "reject after Ready");
            await WaitForFile("stopped.txt");
            using var parent = Process.GetCurrentProcess();
            var prefix = @"Local\TJCloudClient.Update." + parent.Id.ToString(CultureInfo.InvariantCulture) + "." +
                parent.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) + ".";
            using var ready = EventWaitHandle.OpenExisting(prefix + "Ready");
            using var stopped = EventWaitHandle.OpenExisting(prefix + "Stopped");
            Assert.True(ready.WaitOne(0) && stopped.WaitOne(0), "Both Ready and terminal Stopped must be signalled");
            terminalConfirmed = true;
            // Observe native process termination before deleting its mapped files.
            Assert.True(await Task.Run(() => child!.WaitForExit(5000)), "Harmless child did not terminate");
            Assert.Equal(0, child!.ExitCode);
        }
        public Task CloseAsync() { Closed++; return Task.CompletedTask; }
        public async Task CloseAsync(Action commit, CancellationToken token)
        {
            CloseAttempts++;
            if (boundary == "before-commit") await Reject();
            token.ThrowIfCancellationRequested();
            commit();
            await CloseAsync();
        }
        public Task ResumeAsync()
        {
            Assert.True(terminalConfirmed, "Resume requires confirmed Ready and terminal Stopped");
            Resumed++;
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            if (child == null) return;
            try { Assert.True(child.WaitForExit(5000), "Harmless child still owns fixture files"); }
            finally { child.Dispose(); }
        }
    }

    private sealed class FaultLifetime(string helper, string download, string mode, CancellationTokenSource cancellation) : IUpdateLifetime
    {
        public int Resumed;
        public Process? Child;
        public Task<bool> PrepareAsync(CancellationToken token)
        {
            if (mode == "no-ack") File.WriteAllText(Path.Combine(download, "noack.txt"), "synthetic withheld acknowledgement");
            foreach (var file in Directory.GetFiles(helper).Where(x => x.EndsWith(".dll") || x.EndsWith(".runtimeconfig.json") || x.EndsWith(".deps.json")))
                File.Copy(file, Path.Combine(download, Path.GetFileName(file)));
            return Task.FromResult(true);
        }
        public Task CloseAsync() => throw new IOException("synthetic close failure");
        public Task CloseAsync(Action commit, CancellationToken token)
        {
            Child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(download, "child.txt"))));
            _ = Child.Handle;
            if (mode == "late-cancel") cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            commit();
            return CloseAsync();
        }
        public Task ResumeAsync()
        {
            Assert.True(File.Exists(Path.Combine(download, "aborted.txt")), "Resume before helper latched abort");
            Resumed++;
            return Task.CompletedTask;
        }
    }

    public static void RevokeSaveFailure() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        var window = new TjClient.Wpf.MainWindow(fixture.Root, false, null,
            (path, _) => new UpdateCoordinator(path, () => new UpdateConsentTests.CountingBackend()));
        try { await window.AssertRevokeSaveFailure(); }
        finally { window.DisposeTestWindow(); }
    });
}

namespace TjClient.Wpf
{
    public partial class MainWindow
    {
        internal async Task AssertRevokeSaveFailure()
        {
            await updates!.SetConsentAsync(true);
            using var locked = new FileStream(Path.Combine(stateDirectory, "updates.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            AutoUpdateBox.IsChecked = false;
            await ChangeUpdateConsentAsync();
            Assert.True(updates.Consent == false && AutoUpdateBox.IsChecked == false);
            Assert.True(UpdateStatusText.Text.Contains("только до перезапуска"), "Нельзя выдавать несохранённый отзыв за постоянный");
            SetUpdateStatus("");
            Assert.True(UpdateStatusText.Text.Contains("только до перезапуска"), "Поздний результат проверки не должен скрыть предупреждение");
            if (Environment.GetEnvironmentVariable("TJ_RUNTIME_WARNING_VISUAL") is { Length: > 0 } image)
            {
                ShowActivated = false;
                Show();
                await Task.Delay(100);
                UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(image);
                encoder.Save(output);
            }
        }
    }
}
