using TjClient.Updates;

static class UpdateConsentTests
{
    public static void RevocationDetachesGeneration() => DetachedGenerationAsync().GetAwaiter().GetResult();
    private static async Task DetachedGenerationAsync()
    {
        using var fixture = new Fixture();
        await using var coordinator = new UpdateCoordinator(fixture.Root, () => new CountingBackend());
        await coordinator.SetConsentAsync(true);
        var field = typeof(UpdateCoordinator).GetField("authorization", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var before = (CancellationTokenSource)field.GetValue(coordinator)!;
        await coordinator.SetConsentAsync(false);
        var after = (CancellationTokenSource)field.GetValue(coordinator)!;
        Assert.True(!ReferenceEquals(before, after), "Отзыв должен отделить старое поколение до вызова Cancel вне lock");
        await coordinator.SetConsentAsync(true);
        Assert.True(before.IsCancellationRequested && !after.IsCancellationRequested);
        Assert.Equal(UpdateOutcome.UpToDate, await coordinator.CheckAsync());
    }

    public static void BoundedDispose() => BoundedDisposeAsync().GetAwaiter().GetResult();
    private static async Task BoundedDisposeAsync()
    {
        using var fixture = new Fixture();
        var backend = new UncancellableBackend();
        var coordinator = new UpdateCoordinator(fixture.Root, () => backend);
        await coordinator.SetConsentAsync(true);
        var checking = coordinator.CheckAsync();
        var disposing = coordinator.DisposeAsync().AsTask();
        try
        {
            await disposing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(!backend.Disposed, "Не освобождать ресурс незавершённого backend");
        }
        finally { backend.Release.TrySetResult(); await checking; await disposing; }
        for (var i = 0; i < 50 && !backend.Disposed; i++) await Task.Delay(10);
        Assert.True(backend.Disposed);
    }
    private sealed class UncancellableBackend : IUpdateBackend
    {
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public async Task<UpdateOutcome> CheckAsync(CancellationToken _) { await Release.Task; return UpdateOutcome.UpToDate; }
        public void Dispose() => Disposed = true;
    }

    public static void FailureIsNotSuccess() => FailureAsync().GetAwaiter().GetResult();
    private static async Task FailureAsync()
    {
        using var fixture = new Fixture();
        fixture.File("updates.json", "{not-valid-json");
        var created = 0;
        await using var coordinator = new UpdateCoordinator(fixture.Root, () =>
        {
            created++;
            throw new InvalidDataException("synthetic invalid signature/key or network failure");
        });
        Assert.Equal(UpdateOutcome.PermissionRequired, await coordinator.CheckAsync());
        Assert.Equal(0, created);
        await coordinator.SetConsentAsync(true);
        Assert.Equal(UpdateOutcome.Failed, await coordinator.CheckAsync());
        Assert.Equal(1, created);
    }

    public static void RevocationSerializes() => RevocationAsync().GetAwaiter().GetResult();
    private static async Task RevocationAsync()
    {
        using var fixture = new Fixture();
        using var backend = new BlockingBackend();
        await using var coordinator = new UpdateCoordinator(fixture.Root, () => backend);
        await coordinator.SetConsentAsync(true);
        var first = coordinator.CheckAsync();
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = coordinator.CheckAsync();
        try
        {
            Assert.Equal(1, backend.Calls);
            await coordinator.SetConsentAsync(false).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(UpdateOutcome.Cancelled, await first.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)) is UpdateOutcome.Cancelled or UpdateOutcome.PermissionRequired);
            Assert.Equal(1, backend.Calls);
            Assert.True(backend.Cancelled);
        }
        finally { backend.Release.TrySetResult(); await Task.WhenAll(first, second); }
    }

    internal sealed class BlockingBackend : IUpdateBackend
    {
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<UpdateOutcome> CheckAsync(CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return UpdateOutcome.UpToDate;
        }
        public void Dispose() => Release.TrySetResult();
    }

    public static void ConsentIsSeparateAndOffline() => ConsentAsync().GetAwaiter().GetResult();
    private static async Task ConsentAsync()
    {
        using var fixture = new Fixture();
        var sentinel = fixture.File("settings.dpapi", "synthetic original profile bytes");
        var original = File.ReadAllBytes(sentinel);
        var prompts = 0;
        var created = 0;
        var backend = new CountingBackend();
        IUpdateBackend Create() { created++; return backend; }
        await using (var coordinator = new UpdateCoordinator(fixture.Root, Create))
        {
            Assert.Equal<bool?>(null, coordinator.Consent);
            Assert.Equal(UpdateOutcome.PermissionRequired, await coordinator.CheckAsync());
            await coordinator.InitializeAsync(() => { prompts++; return Task.FromResult(false); });
            Assert.Equal(false, coordinator.Consent);
            Assert.Equal(1, prompts);
            Assert.Equal(0, created);
            Assert.Equal(0, backend.Calls);
        }
        await using (var coordinator = new UpdateCoordinator(fixture.Root, Create))
        {
            await coordinator.InitializeAsync(() => { prompts++; return Task.FromResult(true); });
            Assert.Equal(1, prompts);
            Assert.Equal(false, coordinator.Consent);
            await coordinator.SetConsentAsync(true);
            Assert.Equal(UpdateOutcome.UpToDate, await coordinator.CheckAsync());
            Assert.Equal(1, created);
            Assert.Equal(1, backend.Calls);
            await coordinator.SetConsentAsync(false);
            Assert.Equal(UpdateOutcome.PermissionRequired, await coordinator.CheckAsync());
            Assert.Equal(1, backend.Calls);
        }
        Assert.True(original.SequenceEqual(File.ReadAllBytes(sentinel)), "Согласие обновлений не меняет профиль передачи");
        Assert.True(File.Exists(Path.Combine(fixture.Root, "updates.json")));
    }

    internal sealed class CountingBackend : IUpdateBackend
    {
        public int Calls { get; private set; }
        public Task<UpdateOutcome> CheckAsync(CancellationToken token) { Calls++; return Task.FromResult(UpdateOutcome.UpToDate); }
        public void Dispose() { }
    }
}
