using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TjClient.Core;

static class ResponseBodyTests
{
    public static void OverallTimeout() => OverallTimeoutAsync().GetAwaiter().GetResult();
    private static async Task OverallTimeoutAsync()
    {
        await using var server = await BodyServer.Start();
        using var handler = TestTls.CreateHandler(server.Ca);
        using (var warmHttp = new HttpClient(handler, false))
        using (var warmApi = new CollectorClient(server.Address, "synthetic", warmHttp)) await warmApi.Status();
        using var http = new HttpClient(handler, false) { Timeout = TimeSpan.FromMilliseconds(600) };
        using var api = new CollectorClient(server.Address, "synthetic", http);
        await api.Status();
        server.HangStatusBody = true;
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await api.Status(guard.Token);
            throw new Exception("Ожидался таймаут тела ответа");
        }
        catch (TransportException error)
        {
            Assert.True(error.Transient);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "Таймаут должен ограничивать весь запрос, включая тело");
        }
        catch (OperationCanceledException) { throw new Exception("HttpClient.Timeout не ограничил чтение тела HTTPS-ответа"); }
        Assert.True(server.BodyStarted.Task.IsCompletedSuccessfully);
        server.BodyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelHttp = new HttpClient(handler, false) { Timeout = TimeSpan.FromSeconds(10) };
        using var cancelApi = new CollectorClient(server.Address, "synthetic", cancelHttp);
        using var cancellation = new CancellationTokenSource();
        var pending = cancelApi.Status(cancellation.Token);
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Ожидалась отмена пользователем"); }
        catch (OperationCanceledException) { Assert.True(cancellation.IsCancellationRequested); }
    }
    public static void TruncatedAcknowledgement() => TruncatedAcknowledgementAsync().GetAwaiter().GetResult();
    private static async Task TruncatedAcknowledgementAsync()
    {
        await using var server = await BodyServer.Start();
        server.DropChunkReply = true;
        using var fixture = new Fixture();
        var path = fixture.File("rphost_1/26112501.log", "synthetic truncated reply");
        var db = Path.Combine(fixture.Root, "queue.db");
        using var http = TestTls.Create(server.Ca);
        using var api = new CollectorClient(server.Address, "synthetic", http);
        using (var queue = LegacyQueue.Open(db, fixture.Root))
        {
            try { await new ClientEngine(fixture.Root, 1, queue, api).Run(); throw new Exception("Ожидался обрыв ответа"); }
            catch (TransportException error) { Assert.True(error.Transient); Assert.Equal(0, error.StatusCode); }
            var saved = queue.All().Single();
            Assert.Equal(4L, server.Collector.Bytes.Length);
            Assert.Equal(QueueState.Retry, saved.State);
            Assert.Equal(0L, saved.Offset);
            Assert.Equal(1, saved.Attempts);
            Assert.True(saved.NextAttemptTicks > DateTimeOffset.UtcNow.UtcTicks);
            Assert.True(!saved.Error.Contains("upload_id"));
        }
        using (var reopened = new QueueStore(db))
        {
            var saved = reopened.All().Single();
            Assert.Equal(1, saved.Attempts);
            Assert.Equal(0L, saved.Offset);
            reopened.Save(saved with { NextAttemptTicks = 0 });
            await new ClientEngine(fixture.Root, 1, reopened, api).Run();
            Assert.Equal(QueueState.Delivered, reopened.All().Single().State);
            Assert.True(File.ReadAllBytes(path).SequenceEqual(server.Collector.Bytes.ToArray()));
            Assert.Equal(1, server.Collector.Registrations);
            Assert.Equal(1, server.Collector.Completions);
        }
        using var shipping = new CollectorClient(server.Address, "synthetic");
        try { await shipping.Status(); throw new Exception("Ожидался отказ недоверенному CA"); }
        catch (TransportException error) { Assert.True(!error.Transient, "Ошибка сертификата не должна включать автоматический повтор"); }
    }
}

// Только тесты: Kestrel слушает loopback, CA не попадает в системное хранилище.
sealed class BodyServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    public X509Certificate2 Ca { get; }
    public string Address { get; private set; } = "";
    public MemoryCollector Collector { get; } = new();
    public bool DropChunkReply { get; set; }
    public bool HangStatusBody { get; set; }
    public TaskCompletionSource BodyStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private BodyServer(WebApplication app, X509Certificate2 ca, X509Certificate2 certificate)
    { this.app = app; Ca = ca; this.certificate = certificate; }

    public static async Task<BodyServer> Start()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=TJ Synthetic Body Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var ca = X509Certificate2.CreateFromPem(root.ExportCertificatePem());
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var server = new BodyServer(app, ca, certificate);
        app.MapMethods("/api/collector/v1/{**rest}", ["GET", "PUT", "POST"], server.Handle);
        await app.StartAsync();
        server.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost");
        return server;
    }
    private async Task Handle(HttpContext context)
    {
        if (HangStatusBody && context.Request.Path.Value!.EndsWith("/status"))
        {
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 1000;
            await context.Response.WriteAsync("{");
            await context.Response.Body.FlushAsync();
            BodyStarted.TrySetResult();
            try { await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted); }
            catch (OperationCanceledException) { }
            context.Abort();
            return;
        }
        using var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), "https://localhost" + context.Request.Path + context.Request.QueryString)
        { Content = new StreamContent(context.Request.Body) };
        using var response = await Collector.Handle(message);
        if (DropChunkReply && context.Request.Path.Value!.EndsWith("/chunk"))
        {
            DropChunkReply = false;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 1000;
            await context.Response.WriteAsync("{\"upload_id\":\"");
            await context.Response.Body.FlushAsync();
            await Task.Delay(100);
            context.Abort();
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        await response.Content.CopyToAsync(context.Response.Body);
    }
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        certificate.Dispose(); Ca.Dispose();
    }
}
