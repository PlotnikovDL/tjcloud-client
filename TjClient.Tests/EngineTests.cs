using System.Net;
using System.Text.Json;
using TjClient.Core;

static class EngineTests
{
    public static void OversizeDoesNotBlock()
    {
        using var fixture = new Fixture();
        var large = fixture.File("rphost_1/26112501.log", "oversize");
        fixture.File("rphost_1/26112502.log", "x");
        var db = Path.Combine(fixture.Root, "queue.db");
        var server = new MemoryCollector { MaxFileBytes = 2 };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        using (var queue = LegacyQueue.Open(db, fixture.Root))
        {
            var engine = new ClientEngine(fixture.Root, 1, queue, api);
            engine.Run().GetAwaiter().GetResult();
            var error = queue.All()[0];
            Assert.Equal(QueueState.Error, error.State);
            Assert.True(error.Error.Contains("размер", StringComparison.OrdinalIgnoreCase) && error.Error.Contains("Повторить"));
            Assert.Equal(QueueState.Delivered, queue.All()[1].State);
            File.Delete(large);
            engine.Run().GetAwaiter().GetResult();
            Assert.Equal(QueueState.Error, queue.All()[0].State);
        }
        using var reopened = new QueueStore(db);
        server.MaxFileBytes = 100000;
        new ClientEngine(fixture.Root, 1, reopened, api).Run().GetAwaiter().GetResult();
        Assert.Equal(QueueState.Error, reopened.All()[0].State);
        Assert.Equal(1, server.Registrations);
        Assert.Equal(1, server.Completions);
    }
    public static void LostCompleteWithoutSource()
    {
        foreach (var remote in new[] { "queued", "imported" })
        {
            using var fixture = new Fixture();
            var path = fixture.File("rphost_1/26112501.log", "synthetic retention cleanup");
            var db = Path.Combine(fixture.Root, "queue.db");
            var server = new MemoryCollector { LoseCompleteReply = true };
            using var http = new HttpClient(new Handler(server.Handle));
            using var api = new CollectorClient("https://localhost", "synthetic", http);
            using (var queue = LegacyQueue.Open(db, fixture.Root))
            {
                Assert.Throws<TransportException>(() => new ClientEngine(fixture.Root, 1, queue, api).Run().GetAwaiter().GetResult());
                queue.Save(queue.All().Single() with { NextAttemptTicks = 0 });
            }
            File.Delete(path);
            server.State = remote;
            using var reopened = new QueueStore(db);
            new ClientEngine(fixture.Root, 1, reopened, api).Run().GetAwaiter().GetResult();
            if (remote == "queued") Assert.Equal(QueueState.Delivered, reopened.All().Single().State);
            else Assert.Equal(0, reopened.All().Count);
            Assert.Equal(1, server.Gets);
            Assert.Equal(1, server.Registrations);
            Assert.Equal(1, server.Completions);
        }
    }
    public static void AcceptedWithoutRoot()
    {
        foreach (var loseReply in new[] { true, false })
        {
            using var fixture = new Fixture();
            fixture.File("logs/rphost_1/26112501.log", "synthetic unavailable root");
            var root = Path.Combine(fixture.Root, "logs");
            using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), root);
            var server = new MemoryCollector { LoseCompleteReply = loseReply };
            using var http = new HttpClient(new Handler(server.Handle));
            using var api = new CollectorClient("https://localhost", "synthetic", http);
            var engine = new ClientEngine(root, 1, queue, api);
            if (loseReply) Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
            else engine.Run().GetAwaiter().GetResult();
            queue.Save(queue.All().Single() with { NextAttemptTicks = 0 });
            fixture.File("logs/rphost_1/26112502.log", "ещё не отправлено");
            queue.Save(new(LogScanner.Scan(root).Single(x => x.RelativePath.EndsWith("02.log"))));
            Directory.Move(root, root + "-offline");
            server.State = "imported";
            Assert.Throws<DirectoryNotFoundException>(() => engine.Run().GetAwaiter().GetResult());
            Assert.Equal(QueueState.Pending, queue.All().Single().State);
            Assert.Equal(1, server.Gets);
            Assert.Equal(1, server.Registrations);
            Assert.Equal(1, server.Completions);
        }
    }
    public static void UploadingWithoutSource()
    {
        using var fixture = new Fixture();
        var path = fixture.File("rphost_1/26112501.log", "synthetic missing source");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector { LoseChunkReply = true };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
        queue.Save(queue.All().Single() with { NextAttemptTicks = 0 });
        File.Delete(path);
        Assert.Throws<FileNotFoundException>(() => engine.Run().GetAwaiter().GetResult());
        Assert.Equal(1, server.Gets);
        Assert.Equal(4L, server.Bytes.Length);
        Assert.Equal(0, server.Completions);
    }
    public static void StateRegression()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "synthetic");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector();
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        engine.Run().GetAwaiter().GetResult();
        server.State = "uploading";
        Assert.Throws<InvalidDataException>(() => engine.Run().GetAwaiter().GetResult());
        Assert.Equal(QueueState.Delivered, queue.All().Single().State);
        Assert.Equal(1, server.Completions);
    }
    public static void LostComplete()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "synthetic");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector { LoseCompleteReply = true };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
        queue.Save(queue.All().Single() with { NextAttemptTicks = 0 });
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(QueueState.Delivered, queue.All().Single().State);
        Assert.Equal(1, server.Completions);
        Assert.Equal(1, server.Registrations);
    }
    public static void PermanentErrors()
    {
        foreach (var code in new[] { 400, 401, 403, 409 })
        {
            using var fixture = new Fixture();
            fixture.File("rphost_1/26112501.log", "synthetic");
            fixture.File("rphost_1/26112502.log", "synthetic");
            using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
            var server = new MemoryCollector { Intercept = request => request.Method == HttpMethod.Put ? new HttpResponseMessage((HttpStatusCode)code) : null };
            using var http = new HttpClient(new Handler(server.Handle));
            using var api = new CollectorClient("https://localhost", "synthetic", http);
            Assert.Throws<TransportException>(() => new ClientEngine(fixture.Root, 1, queue, api).Run().GetAwaiter().GetResult());
            Assert.Equal(code == 409 ? QueueState.Conflict : QueueState.Error, queue.All()[0].State);
            Assert.Equal(0L, queue.All()[0].NextAttemptTicks);
            Assert.Equal(QueueState.Pending, queue.All()[1].State);
            Assert.Equal(1, server.Registrations);
        }
    }
    public static void ServerSizeLimit()
    {
        using var fixture = new Fixture();
        const string first = "rphost_1/26112501.log";
        fixture.File(first, "synthetic oversized file");
        fixture.File("rphost_1/26112502.log", "x");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector();
        server.Intercept = request => request.Method == HttpMethod.Put && server.Relative == first
            ? new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge) : null;
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        engine.Run().GetAwaiter().GetResult();
        var items = queue.All();
        Assert.Equal(QueueState.Error, items[0].State);
        Assert.Equal(0L, items[0].Offset);
        Assert.True(items[0].UploadId != null && items[0].Error.Contains("лимит"));
        Assert.Equal(QueueState.Delivered, items[1].State);
        engine.Run().GetAwaiter().GetResult();
        Assert.Equal(1, server.Completions);
        Assert.Equal(2, server.Registrations);
    }
    public static void Cancellation()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "synthetic");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        using var cancellation = new CancellationTokenSource();
        var server = new MemoryCollector { Intercept = request => { if (request.Method == HttpMethod.Put) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); } return null; } };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        Assert.Throws<OperationCanceledException>(() => new ClientEngine(fixture.Root, 1, queue, api).Run(cancellation.Token).GetAwaiter().GetResult());
        Assert.Equal(0L, queue.All().Single().Offset);
        Assert.Equal(0, queue.All().Single().Attempts);
    }
    public static void DeliveredConflict()
    {
        using var fixture = new Fixture();
        var path = fixture.File("rphost_1/26112501.log", "synthetic");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector();
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        engine.Run().GetAwaiter().GetResult();
        var originalTime = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, originalTime);
        Assert.Throws<FileChangedException>(() => engine.Run().GetAwaiter().GetResult());
        Assert.Equal(QueueState.Conflict, queue.All().Single().State);
        fixture.File("rphost_1/26112502.log", "next");
        Assert.Throws<FileChangedException>(() => engine.Run().GetAwaiter().GetResult());
        Assert.Equal(1, server.Registrations);
    }
    public static void Backoff()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "synthetic");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector { Intercept = request => request.Method == HttpMethod.Put ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
        queue.Save(queue.All().Single() with { NextAttemptTicks = 0 });
        Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
        var after = queue.All().Single();
        Assert.Equal(2, after.Attempts);
        Assert.True(after.NextAttemptTicks > DateTimeOffset.UtcNow.AddSeconds(9).UtcTicks);
    }
    public static void ChangedFile()
    {
        using var fixture = new Fixture();
        var path = fixture.File("rphost_1/26112501.log", "synthetic-log-data");
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector { LoseChunkReply = true };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        Assert.Throws<TransportException>(() => engine.Run().GetAwaiter().GetResult());
        var before = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, before);
        queue.Save(queue.All().Single() with { NextAttemptTicks = 0 });
        Assert.Throws<FileChangedException>(() => engine.Run().GetAwaiter().GetResult());
        Assert.Equal(QueueState.Conflict, queue.All().Single().State);
        Assert.Equal(4L, server.Bytes.Length);
    }
    public static void Resume() => ResumeAsync().GetAwaiter().GetResult();
    private static async Task ResumeAsync()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "synthetic-log-data");
        var db = Path.Combine(fixture.Root, "queue.db");
        var server = new MemoryCollector { LoseChunkReply = true };
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        using (var queue = LegacyQueue.Open(db, fixture.Root))
        {
            Assert.Throws<TransportException>(() => new ClientEngine(fixture.Root, 1, queue, api).Run().GetAwaiter().GetResult());
            var item = queue.All().Single();
            Assert.Equal(QueueState.Retry, item.State);
            Assert.Equal(0L, item.Offset);
            Assert.True(item.NextAttemptTicks > DateTimeOffset.UtcNow.UtcTicks);
            Assert.Equal(1, item.Attempts);
            queue.Save(item with { NextAttemptTicks = 0 });
        }
        using (var queue = new QueueStore(db))
        {
            await new ClientEngine(fixture.Root, 1, queue, api).Run();
            Assert.Equal(QueueState.Delivered, queue.All().Single().State);
            Assert.Equal(1, server.Registrations);
            Assert.Equal(1, server.Completions);
        }
    }
    public static void Delivery() => DeliveryAsync().GetAwaiter().GetResult();
    private static async Task DeliveryAsync()
    {
        using var fixture = new Fixture();
        var path = fixture.File("rphost_1/26112501.log", "synthetic-log-data");
        var original = File.ReadAllBytes(path);
        using var queue = LegacyQueue.Open(Path.Combine(fixture.Root, "queue.db"), fixture.Root);
        var server = new MemoryCollector();
        using var http = new HttpClient(new Handler(server.Handle));
        using var api = new CollectorClient("https://localhost", "synthetic", http);
        var engine = new ClientEngine(fixture.Root, 1, queue, api);
        await engine.Run();
        Assert.Equal(QueueState.Delivered, queue.All().Single().State);
        Assert.Equal(original.LongLength, queue.All().Single().Offset);
        Assert.True(original.SequenceEqual(server.Bytes.ToArray()));
        Assert.True(original.SequenceEqual(File.ReadAllBytes(path)));
        server.State = "imported";
        await engine.Run();
        Assert.Equal(0, queue.All().Count);
        Assert.Equal(1, server.Completions);
        Assert.Equal(1, server.Registrations);
    }
}

sealed class MemoryCollector
{
    public bool LoseCompleteReply { get; set; }
    public bool LoseChunkReply { get; set; }
    public MemoryStream Bytes { get; } = new();
    public string State { get; set; } = "uploading";
    public string Id { get; } = Guid.NewGuid().ToString();
    public int Registrations { get; private set; }
    public int Completions { get; private set; }
    public int Gets { get; private set; }
    public long MaxFileBytes { get; set; } = 100000;
    public long Size { get; private set; }
    public string Relative { get; private set; } = "";
    public string Hash { get; private set; } = "";
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }
    public async Task<HttpResponseMessage> Handle(HttpRequestMessage request)
    {
        var intercepted = Intercept?.Invoke(request);
        if (intercepted != null) return intercepted;
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/status")) return TransportTests.Json(new { protocol_version = 1, source_id = 1, source_name = "Синтетический", chunk_bytes = 4, max_file_bytes = MaxFileBytes });
        if (path.EndsWith("/uploads"))
        {
            Registrations++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Size = json.RootElement.GetProperty("size_bytes").GetInt64();
            Relative = json.RootElement.GetProperty("relative_path").GetString()!;
            Hash = json.RootElement.GetProperty("sha256").GetString()!;
        }
        else if (request.Method == HttpMethod.Get) Gets++;
        if (path.EndsWith("/chunk"))
        {
            var offset = long.Parse(request.RequestUri.Query.Split('=')[1]);
            Assert.Equal(Bytes.Length, offset);
            using var gzip = new System.IO.Compression.GZipStream(await request.Content!.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
            await gzip.CopyToAsync(Bytes);
            if (LoseChunkReply) { LoseChunkReply = false; throw new HttpRequestException("synthetic lost reply"); }
        }
        if (path.EndsWith("/complete"))
        {
            Assert.Equal(Size, Bytes.Length);
            Assert.Equal(Hash, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes.ToArray())));
            Completions++;
            State = "queued";
            if (LoseCompleteReply) { LoseCompleteReply = false; throw new HttpRequestException("synthetic lost complete reply"); }
        }
        return TransportTests.Json(new { upload_id = Id, relative_path = Relative, size_bytes = Size, received_bytes = Bytes.Length, status = State, job_id = State == "uploading" ? (int?)null : 7, error = (string?)null });
    }
}
