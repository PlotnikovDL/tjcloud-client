using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TjClient.Core;

public sealed record CollectorStatus(int ProtocolVersion, long SourceId, string SourceName, int ChunkBytes, long MaxFileBytes);
public sealed record Upload(string UploadId, string RelativePath, long SizeBytes, long ReceivedBytes, string Status, long? JobId, string? Error);

public sealed class CollectorClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly Uri endpoint;
    private readonly string token;
    public Uri Endpoint => endpoint;
    public CollectorClient(string endpoint, string token, HttpClient? client = null)
    {
        this.endpoint = EndpointPolicy.Parse(endpoint);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl)) throw new ArgumentException("Укажите ключ источника.");
        this.token = token;
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(90) };
        ownsClient = client == null;
    }
    public Task<CollectorStatus> Status(CancellationToken ct = default) => Send<CollectorStatus>(HttpMethod.Get, "status", null, ct);
    public Task<Upload> Register(string relative, long size, string hash, CancellationToken ct = default)
    {
        var content = new StringContent(JsonSerializer.Serialize(new { relative_path = relative, size_bytes = size, sha256 = hash }), System.Text.Encoding.UTF8, "application/json");
        return Send<Upload>(HttpMethod.Post, "uploads", content, ct);
    }
    public async Task<Upload> Chunk(string id, long offset, byte[] data, CancellationToken ct = default)
    {
        if (data.Length > 4 * 1024 * 1024 || data.Length == 0) throw new ArgumentOutOfRangeException(nameof(data));
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true)) await gzip.WriteAsync(data, ct);
        var body = new ByteArrayContent(memory.GetBuffer(), 0, checked((int)memory.Length));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentEncoding.Add("gzip");
        return await Send<Upload>(HttpMethod.Put, $"uploads/{Id(id)}/chunk?offset={offset}", body, ct, Convert.ToHexStringLower(SHA256.HashData(data)));
    }
    public Task<Upload> Complete(string id, CancellationToken ct = default) => Send<Upload>(HttpMethod.Post, $"uploads/{Id(id)}/complete", null, ct);
    public Task<Upload> Get(string id, CancellationToken ct = default) => Send<Upload>(HttpMethod.Get, $"uploads/{Id(id)}", null, ct);
    private static string Id(string id) => Guid.Parse(id).ToString();
    private async Task<T> Send<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct, string? chunkHash = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (client.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);
        try { return await SendCore<T>(method, path, content, deadline.Token, chunkHash); }
        catch (HttpRequestException e)
        {
            ct.ThrowIfCancellationRequested();
            throw new TransportException(0, e.HttpRequestError != HttpRequestError.SecureConnectionError,
                e.HttpRequestError == HttpRequestError.SecureConnectionError ? "Сертификат или TLS-соединение не прошли проверку." : "Сетевая ошибка. Передача будет продолжена после задержки.");
        }
        catch (IOException)
        {
            ct.ThrowIfCancellationRequested();
            throw new TransportException(0, true, "Сетевой обрыв при чтении ответа. Передача будет продолжена после задержки.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TransportException(0, true, "Сервер не ответил вовремя."); }
        catch (JsonException) { throw new InvalidDataException("Некорректный формат ответа сервера."); }
    }
    private async Task<T> SendCore<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct, string? chunkHash)
    {
        using var request = new HttpRequestMessage(method, new Uri(endpoint, "/api/collector/v1/" + path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Requested-With", "TJCloudClient");
        if (chunkHash != null) request.Headers.Add("X-Chunk-SHA256", chunkHash);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            throw new TransportException(code, code is 408 or 429 or 500 or 502 or 503 or 504, code switch
            {
                401 => "Ключ недействителен или отозван. Отправка остановлена.",
                403 => "Источник приостановлен. Отправка остановлена.",
                409 => "Конфликт файла или смещения. Отправка остановлена.",
                410 => "Передача истекла. Требуется явная повторная регистрация.",
                413 => "Превышен лимит сервера.",
                >= 300 and < 400 => "Перенаправление запрещено. Проверьте HTTPS-адрес.",
                _ => $"Сервер отклонил запрос (HTTP {code})."
            });
        }
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + read > 65536) throw new InvalidDataException("Слишком большой ответ сервера.");
            body.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(body.ToArray(), Json) ?? throw new InvalidDataException("Пустой ответ сервера.");
    }
    public void Dispose() { if (ownsClient) client.Dispose(); }
}

