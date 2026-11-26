using TjClient.Core;

namespace TjClient.Wpf;

// Observations and test scheduling only. Drain, transfer and restart algorithms stay production-owned.
public partial class MainWindow
{
    internal void UpdaterAssertActive()
    {
        Dispatcher.VerifyAccess();
        UpdaterIntegration.Require(busy && activeOperations > 0 && cancellation != null, "REAL_CORE_OPERATION_ACTIVE");
    }
    internal void UpdaterAssertDrained()
    {
        Dispatcher.VerifyAccess();
        UpdaterIntegration.Require(updateQuiescing && !busy && activeOperations == 0 && cancellation == null && operationsDrained.Task.IsCompletedSuccessfully, "REAL_CORE_OPERATION_DRAINED");
    }
    internal void UpdaterAssertIntent(bool expected)
    {
        Dispatcher.VerifyAccess();
        UpdaterIntegration.Require(running == expected && profile.Running == expected, "RUNNING_INTENT");
        profile.RequireConsent();
    }
    internal Task UpdaterOneShot() => Cycle();
    internal Task UpdaterWaitCheck() => updateWork.WaitAsync(TimeSpan.FromSeconds(60));
    internal async Task UpdaterWaitIdle()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (busy || activeOperations != 0) await Task.Delay(50, deadline.Token);
    }
}
