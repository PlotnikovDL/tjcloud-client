using System.Reflection;
using System.Windows;
using TjClient.Core;

namespace TjClient.Wpf;

static class UpdateStopTests
{
    public static void DispatcherExitPreservesIntent() => CompactWindowTests.InSta(async () =>
    {
        foreach (var observing in new[] { true, false })
        {
            using var fixture = new Fixture();
            var profile = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost", Token = "synthetic",
                SourceId = 1, Running = observing, Startup = true }.Approve();
            var path = Path.Combine(fixture.Root, "settings.dpapi");
            ProtectedStore.Save(path, profile);
            var original = File.ReadAllBytes(path);
            var window = new MainWindow(fixture.Root, false);
            var lifetime = (TjClient.Updates.IUpdateLifetime)window;
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                try { await lifetime.CloseAsync(); throw new Exception("Ungated close permitted"); }
                catch (InvalidOperationException) { }
                Assert.True(!closed);
                Assert.True(await lifetime.PrepareAsync(CancellationToken.None));
                await lifetime.CloseAsync();
                Assert.True(closed);
                Assert.True(original.SequenceEqual(File.ReadAllBytes(path)));
                Assert.Equal(observing, ProtectedStore.Load<ClientProfile>(path)!.Running);
            }
            finally { if (!closed) window.DisposeTestWindow(); }
        }
    });

    public static void StartDuringDrain() => CompactWindowTests.InSta(async () =>
    {
        foreach (var mode in new[] { "start", "pause", "initialization", "save" })
        {
            using var fixture = new Fixture();
            var saved = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost", Token = "synthetic",
                SourceId = 1, Running = false }.Approve();
            ProtectedStore.Save(Path.Combine(fixture.Root, "settings.dpapi"), saved);
            var window = new MainWindow(fixture.Root, false, _ => throw new Exception("No network expected"));
            try { await window.AssertStartDuringDrain(saved, mode); }
            finally { window.DisposeTestWindow(); }
        }
    });

    public static void TimeoutAndPause() => CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false);
        try { await window.AssertUpdateTimeoutAndPause(); }
        finally { window.DisposeTestWindow(); }
    });

    public static void FailedDrain() => CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        using var handler = new FailedDrainHandler();
        using var http = new HttpClient(handler);
        var saved = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost", Token = "synthetic",
            SourceId = 1, Running = true }.Approve();
        ProtectedStore.Save(Path.Combine(fixture.Root, "settings.dpapi"), saved);
        var window = new MainWindow(fixture.Root, false, value => new CollectorClient(value.Endpoint, value.Token, http));
        try { await window.AssertFailedUpdateDrain(handler); }
        finally { window.DisposeTestWindow(); }
    });

    internal sealed class FailedDrainHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { throw new InvalidOperationException("synthetic drain failure"); }
            throw new InvalidOperationException();
        }
    }

    public static void RealDrain() => CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        await using var server = await BodyServer.Start();
        using var http = TestTls.Create(server.Ca);
        using (var warm = new CollectorClient(server.Address, "synthetic", http)) await warm.Status();
        var saved = new ClientProfile { Folder = fixture.Root, Endpoint = server.Address, Token = "synthetic",
            SourceId = 1, Running = true, Startup = true }.Approve();
        ProtectedStore.Save(Path.Combine(fixture.Root, "settings.dpapi"), saved);
        using (var queue = new QueueStore(Path.Combine(fixture.Root, saved.QueueKey + ".db")))
            queue.BeginObservation(saved.Folder, [], saved.Mode, saved.Since);
        var original = File.ReadAllBytes(Path.Combine(fixture.Root, "settings.dpapi"));
        server.HangStatusBody = true;
        var window = new MainWindow(fixture.Root, false, value => new CollectorClient(value.Endpoint, value.Token, http));
        try
        {
            await window.AssertUpdateDrains(server, saved, original);
        }
        finally { window.DisposeTestWindow(); }
    });
}

public partial class MainWindow
{
    internal async Task AssertStartDuringDrain(ClientProfile saved, string mode)
    {
        checkedProfile = saved;
        if (mode == "initialization") Directory.CreateDirectory(Path.Combine(stateDirectory, saved.QueueKey + ".db"));
        using var locked = mode == "save" ? new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? blocker = null;
        try
        {
            ThreadPool.SetMinThreads(1, 1);
            Assert.True(ThreadPool.SetMaxThreads(1, 1));
            blocker = Task.Run(() => { entered.Set(); release.Wait(); });
            Assert.True(entered.Wait(3000));
            var start = Start(true);
            Assert.True(!ProtectedStore.Load<ClientProfile>(settingsPath)!.Running,
                "Нельзя сохранять Running до завершения baseline");
            var prepare = PrepareForUpdateAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (mode == "pause") Pause();
            release.Set();
            var ready = await prepare;
            await start;
            var disk = ProtectedStore.Load<ClientProfile>(settingsPath)!;
            if (mode is "initialization" or "save")
            {
                Assert.True(!ready, mode + " failure must defer update");
                Assert.True(!await PrepareForUpdateAsync(TimeSpan.FromSeconds(2), CancellationToken.None),
                    mode + " unresolved failure must also defer repeated update");
                Assert.True(!disk.Running && !running, "Не возобновлять незафиксированный Start");
                return;
            }
            Assert.True(ready);
            Assert.Equal(mode == "start", disk.Running);
            Assert.Equal(profile.Running, disk.Running);
            Assert.Equal(saved.QueueKey, disk.QueueKey);
            Assert.Equal(saved.ConsentBinding, disk.ConsentBinding);
            using var queue = OpenQueue(profile);
            Assert.True(queue.Initialized);
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maxWorker, maxIo);
            ThreadPool.SetMinThreads(minWorker, minIo);
            if (blocker != null) await blocker;
        }
    }

    internal async Task AssertUpdateTimeoutAndPause()
    {
        profile = new ClientProfile { Folder = stateDirectory, Endpoint = "https://localhost", Token = "synthetic",
            SourceId = 1, Running = true }.Approve();
        running = true;
        SaveProfile();
        using (var lease = AdmitOperation())
        {
            var original = File.ReadAllBytes(settingsPath);
            var timeout = PrepareForUpdateAsync(TimeSpan.FromMilliseconds(40), CancellationToken.None);
            Assert.True(!await timeout && !exiting && !updateQuiescing && timer.IsEnabled);
            Assert.True(running && profile.Running && original.SequenceEqual(File.ReadAllBytes(settingsPath)));
        }
        using (var lease = AdmitOperation())
        {
            var pending = PrepareForUpdateAsync(TimeSpan.FromMilliseconds(40), CancellationToken.None);
            Assert.True(AdmitOperation() == null);
            Pause();
            Assert.True(!await pending);
            Assert.True(!running && !profile.Running, "Не восстанавливать прежнее Running поверх явной паузы");
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.True(!await PrepareForUpdateAsync(TimeSpan.FromSeconds(1), cancel.Token));
        Assert.True(!updateQuiescing && !exiting);
    }

    internal async Task AssertFailedUpdateDrain(UpdateStopTests.FailedDrainHandler handler)
    {
        var original = File.ReadAllBytes(settingsPath);
        running = true;
        var active = Cycle();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(!await PrepareForUpdateAsync(TimeSpan.FromSeconds(2), CancellationToken.None),
            "Ошибка во время останова должна отложить установку");
        await active;
        Assert.True(profile.Running && running && !exiting && !updateQuiescing);
        Assert.True(original.SequenceEqual(File.ReadAllBytes(settingsPath)), "Ошибка останова не отзывает намерение наблюдения");
    }

    internal async Task AssertUpdateDrains(BodyServer server, ClientProfile saved, byte[] original)
    {
        var prepare = GetType().GetMethod("PrepareForUpdateAsync", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(prepare != null, "Нужен отдельный безопасный останов для обновления без Pause/Exit");
        running = true;
        var active = Cycle();
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(busy);
        var ready = (Task<bool>)prepare!.Invoke(this, [TimeSpan.FromSeconds(3), CancellationToken.None])!;
        Assert.True(await ready);
        Assert.True(active.IsCompletedSuccessfully && !busy && cancellation == null,
            "Готовность только после завершения реальной операции и Dispose очереди/сети");
        Assert.True(profile.Running && profile.Startup);
        Assert.Equal(saved.ConsentBinding, profile.ConsentBinding);
        Assert.Equal(saved.QueueKey, profile.QueueKey);
        Assert.True(original.SequenceEqual(File.ReadAllBytes(settingsPath)), "Останов для обновления не переписывает профиль");
        using (var queue = OpenQueue(profile)) Assert.True(queue.Initialized);
        var requests = server.Collector.Registrations;
        await Cycle();
        Assert.True(!busy, "Новые Cycle запрещены после готовности");
        Assert.Equal(requests, server.Collector.Registrations);
        Assert.True(!CheckButton.IsEnabled && !StartButton.IsEnabled && !SendButton.IsEnabled && !RetryButton.IsEnabled);
    }
}
