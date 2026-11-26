using TjClient.Core;

static class QueueTests
{
    public static void ModifiedSinceBoundary()
    {
        using var fixture = new Fixture();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(fixture.File("rphost_1/26112501.log", "до проверки"), old);
        var preview = LogScanner.Scan(fixture.Root);
        File.SetLastWriteTimeUtc(fixture.File("rphost_1/26112502.log", "между проверкой и стартом"), old);
        File.SetLastWriteTimeUtc(fixture.File("rphost_1/26112503.log", "граница периода"), since.UtcDateTime);
        var db = Path.Combine(fixture.Root, "queue.db");
        using (var queue = new QueueStore(db))
        {
            queue.BeginObservation(fixture.Root, preview, InitialMode.ModifiedSince, since);
            Assert.Equal(2, queue.All().Count(x => x.State == QueueState.Baseline));
            Assert.Equal(1, queue.All().Count(x => x.State == QueueState.Pending));
        }
        File.SetLastWriteTimeUtc(fixture.File("rphost_1/26112504.log", "поздняя публикация старого файла"), old);
        using var reopened = new QueueStore(db);
        reopened.BeginObservation(fixture.Root, [], InitialMode.ModifiedSince, DateTimeOffset.MinValue);
        var server = new MemoryCollector();
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        new ClientEngine(fixture.Root, 1, reopened, api).Run().GetAwaiter().GetResult();
        Assert.Equal(3, reopened.All().Count(x => x.State == QueueState.Baseline));
        Assert.Equal("rphost_1/26112503.log", reopened.All().Single(x => x.State == QueueState.Delivered).File.RelativePath);
        Assert.Equal(1, server.Registrations);
    }
    public static void ObservationBoundary()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "before-preview");
        var preview = LogScanner.Scan(fixture.Root);
        fixture.File("rphost_1/26112502.log", "before-start");
        using var queue = new QueueStore(Path.Combine(fixture.Root, "queue.db"));
        queue.BeginObservation(fixture.Root, preview, InitialMode.NewFiles, null);
        Assert.Equal(2, queue.All().Count(x => x.State == QueueState.Baseline));
        fixture.File("rphost_1/26112503.log", "after-start");
        queue.Discover(LogScanner.Scan(fixture.Root));
        Assert.Equal(1, queue.All().Count(x => x.State == QueueState.Pending));
    }
    public static void Scope()
    {
        using var fixture = new Fixture();
        using var queue = new QueueStore(Path.Combine(fixture.Root, "queue.db"));
        queue.Bind(fixture.Root, "https://localhost", 1);
        queue.Bind(fixture.Root, "https://localhost/", 1);
        Assert.Throws<InvalidOperationException>(() => queue.Bind(fixture.Root, "https://other.example", 1));
        Assert.Throws<InvalidOperationException>(() => queue.Bind(fixture.Root + "other", "https://localhost", 1));
        Assert.Throws<InvalidOperationException>(() => queue.Bind(fixture.Root, "https://localhost", 2));
        fixture.File("rphost_1/26112501.log", "synthetic");
        fixture.File("rphost_1/26112502.log", "synthetic");
        Assert.Throws<QueueFullException>(() => LogScanner.Scan(fixture.Root, 1));
    }
    public static void Persistence()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "old synthetic");
        var baseline = LogScanner.Scan(fixture.Root);
        var db = Path.Combine(fixture.Root, "queue.db");
        using (var queue = new QueueStore(db))
        {
            queue.Initialize(baseline, InitialMode.NewFiles, null);
            Assert.Equal(QueueState.Baseline, queue.All().Single().State);
            fixture.File("rphost_1/26112502.log", "new synthetic");
            queue.Discover(LogScanner.Scan(fixture.Root));
            var item = queue.All().Single(x => x.State == QueueState.Pending);
            queue.Save(item with { UploadId = Guid.NewGuid().ToString(), Offset = 4, Hash = new string('a', 64), State = QueueState.Uploading });
        }
        using (var queue = new QueueStore(db))
        {
            Assert.True(queue.Initialized);
            var item = queue.All().Single(x => x.State == QueueState.Uploading);
            Assert.Equal(4L, item.Offset);
            Assert.Equal(64, item.Hash.Length);
            queue.Initialize(baseline, InitialMode.ModifiedSince, DateTimeOffset.MinValue);
            Assert.Equal(2, queue.All().Count);
        }
        using var small = new QueueStore(Path.Combine(fixture.Root, "small.db"), 1);
        small.Initialize(baseline, InitialMode.ModifiedSince, DateTimeOffset.MinValue);
        Assert.Equal(QueueState.Pending, small.All().Single().State);
        Assert.Throws<QueueFullException>(() => small.Discover(LogScanner.Scan(fixture.Root)));
        Assert.Equal(1, small.All().Count);
    }
}
