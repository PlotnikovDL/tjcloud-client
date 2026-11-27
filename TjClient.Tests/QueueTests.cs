using System.Net;
using System.Text;
using System.Text.Json;
using TjClient.Core;

static class QueueTests
{
    private const string Hour = "rphost_1/26112501.log";
    private const string NextHour = "rphost_1/26112502.log";
    private const string Before = "01:15.000001-0,EXCP,0,Descr=до начала\r\n";
    private const string First = "03:25.000002-12,DBMSSQL,3,Sql='select ''a''\r\nfrom T'\r\n";
    private const string Second = "04:35.000003-1,EXCP,0,Context=\"строка 1\nстрока \"\"2\"\"\"\r\n";
    private const string Last = "00:05.000004-0,EXCP,0,Descr=следующий час\n";

    public static void ObservationBoundary()
    {
        using var fixture = new Fixture();
        string path = fixture.File(Hour, Before);
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var collector = new JournalCollector();
        using var queue = new QueueStore(Path.Combine(fixture.Root, "queue.db"));
        queue.PreviewPeriod(LogScanner.Scan(fixture.Root), InitialMode.NewFiles, null);
        Append(Before);
        queue.ConfirmObservation(fixture.Root, InitialMode.NewFiles, null);
        var engine = new ClientEngine(fixture.Root, 1, queue, collector.Api);
        Append(First[..First.IndexOf("from", StringComparison.Ordinal)]);
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(0, collector.Files.Count);
        Append(First[First.IndexOf("from", StringComparison.Ordinal)..]);
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(First, collector.Texts.Single());
        Append(Second);
        fixture.File(NextHour, Last);
        engine.Run().GetAwaiter().GetResult();
        Assert.True(collector.Texts.Order().SequenceEqual(new[] { First, Second, Last }.Order()));
        Assert.Equal(3, collector.Files.Count);
        Assert.True(collector.Files.Values.All(x => LogScanner.IsLogPath(x.Relative)));
        Assert.True(collector.Files.Values.Single(x => x.Relative.EndsWith(NextHour)).Bytes.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(Last)));
        collector.Import();
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(0, queue.All().Count);
        Assert.True(collector.Files.Values.All(x => x.Completions == 1 && x.Registrations == 1));
        void Append(string value) { writer.Write(Encoding.UTF8.GetBytes(value)); writer.Flush(); }
    }
    public static void ModifiedSinceBoundary()
    {
        using var fixture = new Fixture();
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(fixture.File(Hour, Before), since.UtcDateTime.AddTicks(-1));
        File.SetLastWriteTimeUtc(fixture.File(NextHour, First), since.UtcDateTime);
        using var collector = new JournalCollector();
        using var queue = new QueueStore(Path.Combine(fixture.Root, "queue.db"));
        queue.BeginObservation(fixture.Root, InitialMode.ModifiedSince, since);
        File.SetLastWriteTimeUtc(fixture.File("rphost_2/26112501.log", Last), since.UtcDateTime.AddDays(-1));
        var engine = new ClientEngine(fixture.Root, 1, queue, collector.Api);
        engine.Run().GetAwaiter().GetResult();
        File.AppendAllText(Path.Combine(fixture.Root, Hour), Second);
        engine.Run().GetAwaiter().GetResult();
        Assert.True(collector.Texts.Order().SequenceEqual(new[] { First, Second }.Order()));
    }
    public static void ConfirmedPeriod()
    {
        using var fixture = new Fixture();
        int split = First.IndexOf("from", StringComparison.Ordinal);
        string path = fixture.File(Hour, Before + First[..split]);
        using var collector = new JournalCollector();
        var db = Path.Combine(fixture.Root, "queue.db");
        using (var queue = new QueueStore(db))
        {
            queue.BeginObservation(fixture.Root, InitialMode.NewFiles, null);
            File.AppendAllText(path, First[split..] + Second);
            var engine = new ClientEngine(fixture.Root, 1, queue, collector.Api);
            engine.Run().GetAwaiter().GetResult();
            Assert.Equal(Second, collector.Texts.Single());
            collector.Import();
            engine.Run().GetAwaiter().GetResult();
            var preview = queue.PreviewPeriod(LogScanner.Scan(fixture.Root), InitialMode.ModifiedSince, DateTimeOffset.MinValue);
            Assert.Equal(QueueState.Pending, preview.Single().State);
            Assert.Equal(0, queue.All().Count);
            queue.ConfirmObservation(fixture.Root, InitialMode.ModifiedSince, DateTimeOffset.MinValue);
        }
        using var reopened = new QueueStore(db);
        new ClientEngine(fixture.Root, 1, reopened, collector.Api).Run().GetAwaiter().GetResult();
        Assert.True(collector.Texts.Order().SequenceEqual(new[] { Before + First, Second }.Order()));
    }
    public static void BaselineBom()
    {
        using var fixture = new Fixture();
        string path = fixture.File(Hour, "");
        File.WriteAllBytes(path, [0xef, 0xbb, 0xbf]);
        using var collector = new JournalCollector();
        using var queue = new QueueStore(Path.Combine(fixture.Root, "queue.db"));
        queue.BeginObservation(fixture.Root, InitialMode.NewFiles, null);
        File.AppendAllText(path, First);
        var engine = new ClientEngine(fixture.Root, 1, queue, collector.Api);
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(First, collector.Texts.Single());
        queue.ConfirmObservation(fixture.Root, InitialMode.ModifiedSince, DateTimeOffset.MinValue);
        collector.Import();
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(0, queue.All().Count);
        Assert.Equal(1, collector.Files.Count);
    }

    public static void UpgradeInFlight()
    {
        using var fixture = new Fixture();
        string path = fixture.File(Hour, First);
        fixture.File(NextHour, Before);
        fixture.File("rphost_2/26112502.log", Last);
        var bytes = Encoding.UTF8.GetBytes(First);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        using var collector = new JournalCollector();
        var old = collector.Api.Register(Hour, bytes.Length, hash, default).GetAwaiter().GetResult();
        collector.Api.Chunk(old.UploadId, 0, bytes[..4], default).GetAwaiter().GetResult();
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root, f =>
            f.RelativePath == Hour ? new(f, Hash: hash, UploadId: old.UploadId, Offset: 4, State: QueueState.Uploading) :
            new(f, State: f.RelativePath == NextHour ? QueueState.Imported : QueueState.Baseline));
        Assert.Equal(old.UploadId, queue.All().Single().UploadId);
        File.AppendAllText(path, Second);
        var engine = new ClientEngine(fixture.Root, 1, queue, collector.Api);
        engine.Run().GetAwaiter().GetResult();
        Assert.True(collector.Texts.Order().SequenceEqual(new[] { First, Second }.Order()));
        Assert.Equal(old.UploadId, collector.Files[Hour].Id);
        Assert.Equal(1, collector.Files[Hour].Registrations);
        collector.Import();
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(0, queue.All().Count);
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
    }
    public static void Persistence()
    {
        using var fixture = new Fixture();
        var large = "13:45.123456-1,TLOCK,3,Locks='" + new string('x', 135000) + "\r\nконец'\r\n";
        string path = fixture.File(Hour, large);
        string db = Path.Combine(fixture.Root, "queue.db");
        using var collector = new JournalCollector { Offline = true, LoseChunk = true };
        using (var queue = new QueueStore(db, 1))
        {
            queue.BeginObservation(fixture.Root, InitialMode.ModifiedSince, DateTimeOffset.MinValue);
            var engine = new ClientEngine(fixture.Root, 1, queue, collector.Api);
            Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
            Assert.Equal(QueueState.Pending, queue.All().Single().State);
            File.Delete(path);
            fixture.File(NextHour, Last);
            collector.Offline = false;
            Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
            var retry = queue.All().Single();
            Assert.Equal(QueueState.Retry, retry.State);
            Assert.Equal(0L, retry.Offset);
            queue.Save(retry with { NextAttemptTicks = 0 });
        }
        using var reopened = new QueueStore(db, 1);
        var restored = new ClientEngine(fixture.Root, 1, reopened, collector.Api);
        restored.Run().GetAwaiter().GetResult();
        Assert.Equal(large, collector.Texts.Single());
        collector.Import();
        restored.Run().GetAwaiter().GetResult();
        Assert.Equal(1, reopened.All().Count);
        Assert.Equal(Last, collector.Texts.Single(x => x == Last));
        collector.Import();
        restored.Run().GetAwaiter().GetResult();
        Assert.Equal(0, reopened.All().Count);
        Assert.True(collector.Files.Values.All(x => x.Completions == 1 && x.Registrations == 1));
    }

    private sealed class JournalCollector : IDisposable
    {
        public Dictionary<string, MemoryCollector> Files { get; } = new();
        public IEnumerable<string> Texts => Files.Values.Select(x => Encoding.UTF8.GetString(x.Bytes.ToArray()));
        public CollectorClient Api { get; }
        public bool Offline { get; set; }
        public bool LoseChunk { get; set; }
        private readonly HttpClient http;
        public JournalCollector()
        {
            http = new HttpClient(new Handler(Handle));
            Api = new CollectorClient("https://localhost", "synthetic", http);
        }
        public void Import() { foreach (var file in Files.Values.Where(x => x.State == "queued")) file.State = "imported"; }
        private async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
        {
            if (Offline) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/status")) return TransportTests.Json(new { protocol_version = 1, source_id = 1, source_name = "Тест", chunk_bytes = 49152, max_file_bytes = 2_000_000 });
            MemoryCollector file;
            if (path.EndsWith("/uploads"))
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                string key = json.RootElement.GetProperty("relative_path").GetString()!;
                if (!Files.TryGetValue(key, out var existing))
                {
                    file = new MemoryCollector { LoseChunkReply = LoseChunk };
                    LoseChunk = false;
                    Files.Add(key, file);
                }
                else file = existing;
            }
            else file = Files.Values.Single(x => path.Split('/').Contains(x.Id));
            return await file.Handle(request);
        }
        public void Dispose() { Api.Dispose(); http.Dispose(); }
    }
}
