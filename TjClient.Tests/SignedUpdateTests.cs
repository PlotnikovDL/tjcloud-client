using System.Net;
using System.Security.Cryptography;
using System.Text;
using Chaos.NaCl;
using TjClient.Updates;

static class SignedUpdateTests
{
    public static void RejectInvalidUpdates() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        foreach (var change in new Action<Feed>[]
        {
            f => f.Os = "windows-arm64", f => f.Channel = "beta", f => f.Version = "v1.0.0",
            f => f.Version = "1.0.0-beta", f => f.Url = "https://evil.example/setup.exe",
            f => f.Url = "http://github.com/PlotnikovDL/tjcloud-client/releases/download/v1.0.0/setup.exe",
            f => f.CorruptFeed = true, f => f.CorruptPayload = true, f => f.MissingSignature = true
        })
        {
            using var fixture = new Fixture();
            using var feed = new Feed();
            change(feed);
            using var http = new HttpClient(feed);
            var lifetime = new Lifetime();
            var launches = 0;
            using var backend = new SignedUpdateBackend(fixture.Root, "0.9.0", feed.PublicKey, lifetime, http,
                _ => { launches++; return Task.CompletedTask; });
            Assert.Equal(UpdateOutcome.Failed, await backend.CheckAsync(CancellationToken.None));
            Assert.Equal(0, launches);
            Assert.Equal(0, lifetime.Prepared);
        }
        using var oldFixture = new Fixture();
        using var oldFeed = new Feed();
        using var oldHttp = new HttpClient(oldFeed);
        using var oldBackend = new SignedUpdateBackend(oldFixture.Root, "1.0.0", oldFeed.PublicKey, new Lifetime(), oldHttp,
            _ => throw new Exception("Downgrade/reinstall forbidden"));
        Assert.Equal(UpdateOutcome.UpToDate, await oldBackend.CheckAsync(CancellationToken.None));
        Assert.Equal(2, oldFeed.Requests);
    });

    public static void DrainFailuresAndReverification() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        foreach (var mode in new[] { "exception", "refusal", "tamper", "launch" })
        {
            using var fixture = new Fixture();
            using var feed = new Feed();
            using var http = new HttpClient(feed);
            var lifetime = new Lifetime();
            var launches = 0;
            lifetime.OnPrepare = () =>
            {
                if (mode == "exception") throw new IOException("synthetic preparation failure");
                if (mode == "tamper") File.WriteAllText(Directory.GetFiles(Path.Combine(fixture.Root, "updates"), "*.exe").Single(), "tampered after drain");
                return Task.FromResult(mode != "refusal");
            };
            using var backend = new SignedUpdateBackend(fixture.Root, "0.9.0", feed.PublicKey, lifetime, http,
                _ => { launches++; throw new IOException("synthetic launch failure"); });
            Assert.True(await backend.CheckAsync(CancellationToken.None) is UpdateOutcome.Deferred or UpdateOutcome.Failed);
            Assert.Equal(mode == "launch" ? 1 : 0, launches);
            Assert.Equal(0, lifetime.Closed);
            Assert.Equal(1, lifetime.Resumed);
        }
    });

    public static void FailedCloseRetainsOwnership() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        using var feed = new Feed();
        using var http = new HttpClient(feed);
        var lifetime = new Lifetime { FailClose = true };
        var launches = 0;
        using var backend = new SignedUpdateBackend(fixture.Root, "0.9.0", feed.PublicKey, lifetime, http,
            _ => { launches++; return Task.CompletedTask; });
        Assert.Equal(UpdateOutcome.Failed, await backend.CheckAsync(CancellationToken.None));
        Assert.Equal(UpdateOutcome.Deferred, await backend.CheckAsync(CancellationToken.None));
        Assert.Equal(1, launches);
        Assert.Equal(0, lifetime.Resumed);
    });

    public static void LateCancellation() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        using var feed = new Feed { Payload = new byte[32 * 1024 * 1024] };
        using var http = new HttpClient(feed);
        using var cancellation = new CancellationTokenSource();
        var lifetime = new Lifetime();
        Thread? revoker = null;
        lifetime.OnPrepare = () =>
        {
            revoker = new Thread(() => { Thread.Sleep(20); cancellation.Cancel(); });
            revoker.Start();
            return Task.FromResult(true);
        };
        var launches = 0;
        using var backend = new SignedUpdateBackend(fixture.Root, "0.9.0", feed.PublicKey, lifetime, http,
            _ => { launches++; return Task.CompletedTask; });
        var result = await backend.CheckAsync(cancellation.Token);
        revoker!.Join();
        Assert.Equal(0, launches);
        Assert.Equal(UpdateOutcome.Cancelled, result);
        Assert.Equal(0, lifetime.Closed);
        Assert.Equal(1, lifetime.Resumed);
    });

    public static void ValidUpdate() => TjClient.Wpf.CompactWindowTests.InSta(async () =>
    {
        using var fixture = new Fixture();
        using var feed = new Feed();
        using var http = new HttpClient(feed);
        var lifetime = new Lifetime();
        var launches = 0;
        using var backend = new SignedUpdateBackend(fixture.Root, "0.9.0", feed.PublicKey, lifetime, http, path =>
        {
            Assert.True(File.ReadAllBytes(path).SequenceEqual(feed.Payload));
            Assert.Equal(1, lifetime.Prepared);
            launches++;
            return Task.CompletedTask;
        });
        Assert.Equal(UpdateOutcome.InstallerLaunched, await backend.CheckAsync(CancellationToken.None));
        Assert.Equal(1, launches);
        Assert.Equal(1, lifetime.Closed);
        Assert.Equal(0, lifetime.Resumed);
        Assert.Equal(3, feed.Requests);
    });

    internal sealed class Lifetime : IUpdateLifetime
    {
        public int Prepared, Closed, Resumed;
        public bool FailClose;
        public Func<Task<bool>> OnPrepare = () => Task.FromResult(true);
        public async Task<bool> PrepareAsync(CancellationToken token) { Prepared++; return await OnPrepare(); }
        public Task CloseAsync() { Closed++; if (FailClose) throw new IOException("synthetic close failure"); return Task.CompletedTask; }
        public Task ResumeAsync() { Resumed++; return Task.CompletedTask; }
    }

    internal sealed class Feed : HttpMessageHandler
    {
        private readonly byte[] seed = RandomNumberGenerator.GetBytes(32);
        public byte[] Payload = Encoding.UTF8.GetBytes("synthetic installer bytes - never execute");
        public string PublicKey => Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed));
        public int Requests;
        public string Version = "1.0.0", Os = "windows-x64", Channel = "stable";
        public string Url = "https://github.com/PlotnikovDL/tjcloud-client/releases/download/v1.0.0/TJCloudClient-1.0.0-win-x64-setup.exe";
        public bool CorruptFeed = false, CorruptPayload = false, MissingSignature = false;
        public string Sign(byte[] bytes)
        {
            var signer = new Ed25519Signer();
            signer.InitWithNonExpandedPrivateKey(Ed25519.PublicKeyFromSeed(seed), seed);
            signer.AddToBuffer(bytes, 0, bytes.Length);
            return Convert.ToBase64String(signer.GenerateSignature());
        }
        private byte[] Xml() => Encoding.UTF8.GetBytes($"<rss version=\"2.0\" xmlns:sparkle=\"http://www.andymatuschak.org/xml-namespaces/sparkle\"><channel><item><sparkle:channel>{Channel}</sparkle:channel><enclosure url=\"{Url}\" length=\"{Payload.Length}\" sparkle:version=\"{Version}\" sparkle:os=\"{Os}\" sparkle:signature=\"{Sign(Payload)}\" /></item></channel></rss>");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            Assert.True(request.Headers.Authorization == null);
            byte[] data;
            if (request.RequestUri!.AbsoluteUri == SignedUpdateBackend.FeedUrl)
                data = CorruptFeed ? Encoding.UTF8.GetBytes("<tampered/>") : Xml();
            else if (request.RequestUri.AbsoluteUri == SignedUpdateBackend.FeedUrl + ".signature")
                data = Encoding.UTF8.GetBytes(MissingSignature ? "" : Sign(Xml()));
            else if (request.RequestUri.AbsoluteUri == Url)
                data = CorruptPayload ? [0, 1, 2] : Payload;
            else throw new InvalidOperationException("Unexpected synthetic request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
        protected override void Dispose(bool disposing) { CryptographicOperations.ZeroMemory(seed); base.Dispose(disposing); }
    }
}
