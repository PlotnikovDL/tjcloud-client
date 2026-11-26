using System.Reflection;
using TjClient.Updates;

internal static class UpdaterHarnessRegressionTests
{
    public static void AuthorizationConnected() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        using var feed = new SignedUpdateTests.Feed();
        using var http = new HttpClient(feed);
        var lifetime = new SignedUpdateTests.Lifetime { OnPrepare = () => Task.FromResult(false) };
        await using var coordinator = UpdaterIntegration.CreateCoordinator(fixture.Root, feed.Version, feed.PublicKey, lifetime, http);
        await coordinator.SetConsentAsync(true);
        Assert.Equal(UpdateOutcome.UpToDate, await coordinator.CheckAsync());
        Assert.Equal(2, feed.Requests);
        var backend = typeof(UpdateCoordinator).GetField("backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);
        Assert.True(backend is SignedUpdateBackend, "HARNESS01: coordinator must receive the actual SignedUpdateBackend");
        var authorization = ((SignedUpdateBackend)backend!).AuthorizeHandoff;
        Assert.True(authorization != null, "HARNESS01: production authorization callback missing");
        Assert.True(ReferenceEquals(coordinator, authorization!.Target), "HARNESS01: callback must belong to this coordinator");
        Assert.Equal(typeof(UpdateCoordinator), authorization.Method.DeclaringType);
        Assert.Equal("ExecuteAuthorized", authorization.Method.Name);
        Assert.Equal(0, lifetime.Prepared);
        Assert.Equal(0, lifetime.Closed);
    });

    public static void TamperRejected() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        using var feed = new SignedUpdateTests.Feed();
        using var tampered = new TamperedDownload(feed);
        using var http = new HttpClient(tampered);
        var lifetime = new SignedUpdateTests.Lifetime { OnPrepare = () => Task.FromResult(false) };
        await using var coordinator = UpdaterIntegration.CreateCoordinator(fixture.Root, UpdaterIntegration.OldVersion, feed.PublicKey, lifetime, http);
        await coordinator.SetConsentAsync(true);
        // Match the automatic check followed by the explicit outcome observation in the hosted harness.
        Assert.Equal(UpdateOutcome.Failed, await coordinator.CheckAsync());
        Assert.Equal(UpdateOutcome.Failed, await coordinator.CheckAsync());
        Assert.Equal(2, tampered.Downloads);
        Assert.Equal(6, feed.Requests);
        Assert.Equal(0, lifetime.Prepared);
        Assert.Equal(0, lifetime.Closed);
        Assert.Equal(0, lifetime.Resumed);
        Assert.Equal(0, Directory.GetFiles(Path.Combine(fixture.Root, "updates"), "*.exe").Length);
    });

    private sealed class TamperedDownload(SignedUpdateTests.Feed feed) : DelegatingHandler(feed)
    {
        internal int Downloads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsoluteUri == feed.Url)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                bytes[^1] ^= 1;
                response.Content.Dispose();
                response.Content = new ByteArrayContent(bytes);
                Downloads++;
            }
            return response;
        }
    }
}
