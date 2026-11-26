using System.Net;
using System.Text.Json;
using TjClient.Core;

static class TransportTests
{
    public static void Errors()
    {
        foreach (var code in new[] { 301, 307, 400, 401, 403, 404, 409, 410, 413, 408, 429, 500, 502, 503, 504 })
        {
            using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent("synthetic-secret-response-body") })));
            using var client = new CollectorClient("https://localhost", "synthetic-test-token", http);
            try { client.Status().GetAwaiter().GetResult(); throw new Exception("Ожидался отказ"); }
            catch (TransportException error)
            {
                Assert.Equal(code is 408 or 429 or 500 or 502 or 503 or 504, error.Transient);
                Assert.True(!error.Message.Contains("synthetic-secret"));
                Assert.Equal(code, error.StatusCode);
            }
        }
    }
    public static void Protocol() => Run().GetAwaiter().GetResult();
    private static async Task Run()
    {
        var requests = new List<string>();
        var id = Guid.NewGuid().ToString();
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("Bearer synthetic-test-token", request.Headers.Authorization!.ToString());
            Assert.Equal("TJCloudClient", request.Headers.GetValues("X-Requested-With").Single());
            requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath.EndsWith("/status"))
                return Json(new { protocol_version = 1, source_id = 1, source_name = "Синтетический", chunk_bytes = 4, max_file_bytes = 100 });
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("gzip", request.Content!.Headers.ContentEncoding.Single());
                Assert.Equal("application/octet-stream", request.Content.Headers.ContentType!.MediaType);
                using var gzip = new System.IO.Compression.GZipStream(await request.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress);
                using var bytes = new MemoryStream();
                await gzip.CopyToAsync(bytes);
                Assert.Equal("abc", System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())), request.Headers.GetValues("X-Chunk-SHA256").Single());
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/uploads"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("rphost_1/26112501.log", body.RootElement.GetProperty("relative_path").GetString());
                Assert.Equal(3L, body.RootElement.GetProperty("size_bytes").GetInt64());
                Assert.Equal(3, body.RootElement.EnumerateObject().Count());
            }
            return Json(new { upload_id = id, relative_path = "rphost_1/26112501.log", size_bytes = 3, received_bytes = 3, status = "queued", job_id = 7, error = (string?)null });
        }));
        using var client = new CollectorClient("https://localhost:8443", "synthetic-test-token", http);
        Assert.Equal(1, (await client.Status()).ProtocolVersion);
        await client.Register("rphost_1/26112501.log", 3, new string('a', 64));
        await client.Chunk(id, 0, "abc"u8.ToArray());
        Assert.Equal("queued", (await client.Complete(id)).Status);
        await client.Get(id);
        Assert.Equal($"PUT /api/collector/v1/uploads/{id}/chunk?offset=0", requests[2]);
        Assert.Equal($"POST /api/collector/v1/uploads/{id}/complete", requests[3]);
        Assert.Equal($"GET /api/collector/v1/uploads/{id}", requests[4]);
    }
    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
}

sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
}
