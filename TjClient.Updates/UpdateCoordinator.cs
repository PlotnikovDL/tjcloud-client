namespace TjClient.Updates;

public enum UpdateOutcome { PermissionRequired, UpToDate, InstallerLaunched, Deferred, Failed, Cancelled }

public interface IUpdateBackend : IDisposable
{
    Task<UpdateOutcome> CheckAsync(CancellationToken cancellationToken);
}

public sealed class UpdateCoordinator : IAsyncDisposable
{
    private readonly string preferencesPath;
    private readonly Func<IUpdateBackend> createBackend;
    private IUpdateBackend? backend;
    private bool? consent;
    private readonly object sync = new();
    private readonly SemaphoreSlim checks = new(1, 1);
    private readonly SemaphoreSlim initialization = new(1, 1);
    private CancellationTokenSource authorization = new();
    private readonly List<CancellationTokenSource> retired = [];
    private bool disposed;
    private Task cleanup = Task.CompletedTask;

    public UpdateCoordinator(string stateDirectory, Func<IUpdateBackend> createBackend)
    {
        preferencesPath = Path.Combine(Path.GetFullPath(stateDirectory), "updates.json");
        this.createBackend = createBackend;
        try
        {
            if (!File.Exists(preferencesPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath));
            if (saved?.Schema == 1) consent = saved.AutomaticUpdates;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Missing/unreadable/corrupt consent never authorizes an updater request.
            consent = null;
        }
    }
    public bool? Consent { get { lock (sync) return consent; } }
    public async Task InitializeAsync(Func<Task<bool>> requestConsent)
    {
        await initialization.WaitAsync();
        try
        {
            if (Consent != null) return;
            var decision = await requestConsent();
            if (Consent == null) await SetConsentAsync(decision);
        }
        finally { initialization.Release(); }
    }
    public Task SetConsentAsync(bool enabled)
    {
        CancellationTokenSource? revoked = null;
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!enabled)
                {
                    consent = false;
                    revoked = authorization;
                    retired.Add(authorization);
                    authorization = new();
                }
                SavePreferences(enabled);
                consent = enabled;
            }
        }
        finally { revoked?.Cancel(); }
        return Task.CompletedTask;
    }
    private void SavePreferences(bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(preferencesPath)!);
        var temporary = preferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new Preferences(1, enabled)));
            File.Move(temporary, preferencesPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<UpdateOutcome> CheckAsync()
    {
        CancellationToken token;
        lock (sync)
        {
            if (disposed || consent != true) return UpdateOutcome.PermissionRequired;
            token = authorization.Token;
        }
        var entered = false;
        try
        {
            await checks.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            backend ??= createBackend();
            if (backend is SignedUpdateBackend signed) signed.AuthorizeHandoff = ExecuteAuthorized;
            return await backend.CheckAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return UpdateOutcome.Cancelled; }
        catch { return UpdateOutcome.Failed; }
        finally { if (entered) checks.Release(); }
    }
    private void ExecuteAuthorized(Action action, CancellationToken token)
    {
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (disposed || consent != true || token != authorization.Token) throw new OperationCanceledException(token);
            action();
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        try { authorization.Cancel(); } catch (AggregateException) { }
        cleanup = CleanupAsync();
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (TimeoutException) { /* Cleanup remains observed and owns resources until the operation completes. */ }
    }
    private async Task CleanupAsync()
    {
        await checks.WaitAsync();
        try
        {
            backend?.Dispose();
            authorization.Dispose();
            foreach (var source in retired) source.Dispose();
        }
        catch { /* No unobserved task exception during application shutdown. */ }
        finally { checks.Release(); }
    }
    private sealed record Preferences(int Schema, bool? AutomaticUpdates);
}
