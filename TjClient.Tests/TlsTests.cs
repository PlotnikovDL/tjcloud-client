using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TjClient.Core;

static class TlsTests
{
    public static void CertificateValidation() => Run().GetAwaiter().GetResult();
    private static async Task Run()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=TJ Synthetic Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var withKey = issued.CopyWithPrivateKey(key);
        using var serverCert = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(serverCert)));
        await using var app = builder.Build();
        var redirect = false;
        var redirected = 0;
        app.MapGet("/api/collector/v1/status", () => redirect
            ? Microsoft.AspNetCore.Http.Results.Redirect("/forbidden-target", permanent: false, preserveMethod: true)
            : Microsoft.AspNetCore.Http.Results.Json(new { protocol_version = 1, source_id = 1, source_name = "Synthetic", chunk_bytes = 4194304, max_file_bytes = 2147483648L }));
        app.MapGet("/forbidden-target", () => { redirected++; return "not allowed"; });
        var collector = new MemoryCollector();
        app.MapMethods("/api/collector/v1/uploads/{**rest}", ["GET", "PUT", "POST"], async (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            Assert.Equal("Bearer synthetic-harness-token", context.Request.Headers.Authorization.ToString());
            Assert.Equal("TJCloudClient", context.Request.Headers["X-Requested-With"].ToString());
            using var message = new HttpRequestMessage(new HttpMethod(context.Request.Method), "https://localhost" + context.Request.Path + context.Request.QueryString);
            message.Content = new StreamContent(context.Request.Body);
            using var response = await collector.Handle(message);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = "application/json";
            await response.Content.CopyToAsync(context.Response.Body);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().Replace("127.0.0.1", "localhost");
        using var http = TestTls.Create(ca);
        using var api = new CollectorClient(address, "synthetic", http);
        Assert.Equal(1, (await api.Status()).ProtocolVersion);
        using var wrongName = new CollectorClient(address.Replace("localhost", "127.0.0.1"), "synthetic", http);
        Assert.Throws<TransportException>(() => wrongName.Status().GetAwaiter().GetResult());
        using var shipping = new CollectorClient(address, "synthetic");
        Assert.Throws<TransportException>(() => shipping.Status().GetAwaiter().GetResult());
        using var wrongKey = RSA.Create(2048);
        var wrongRequest = new CertificateRequest("CN=Wrong Synthetic CA", wrongKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        wrongRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var wrongCa = wrongRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using var badHttp = TestTls.Create(wrongCa);
        using var badApi = new CollectorClient(address, "synthetic", badHttp);
        Assert.Throws<TransportException>(() => badApi.Status().GetAwaiter().GetResult());
        redirect = true;
        Assert.Throws<TransportException>(() => api.Status().GetAwaiter().GetResult());
        Assert.Equal(0, redirected);
        redirect = false;
        using var fixture = new Fixture();
        fixture.File("logs/rphost_1/26112501.log", "00:01.000001-0,EXCP,0,Descr=synthetic\n");
        var caPath = fixture.File("ca.crt", ca.ExportCertificatePem());
        var jsonPath = fixture.File("synthetic-config.json", System.Text.Json.JsonSerializer.Serialize(new IntegrationConfig
        {
            Endpoint = address, Token = "synthetic-harness-token", Folder = Path.Combine(fixture.Root, "logs"), CaFile = caPath,
            StateDirectory = Path.Combine(fixture.Root, "state"), Mode = InitialMode.ModifiedSince, SinceUtc = DateTimeOffset.MinValue,
            RecipientConsent = true, MaxCycles = 1, ExpectedFiles = 1
        }));
        Assert.Equal(0, IntegrationHarness.Protect(jsonPath));
        File.Delete(jsonPath);
        Assert.Equal(0, await IntegrationHarness.Run(jsonPath + ".dpapi"));
        Assert.Equal(0, await IntegrationHarness.Run(jsonPath + ".dpapi"));
        Assert.Equal(1, collector.Completions);
        Assert.Equal(1, collector.Registrations);
        await app.StopAsync();
    }
}
