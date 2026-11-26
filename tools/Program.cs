using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Chaos.NaCl;

if (args.Length == 0 && Environment.GetEnvironmentVariable("TJCLIENT_RESTART_PROBE_REPORT") is { Length: > 0 } report)
{
    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        throw new InvalidOperationException("Restart probe requires a hosted runner.");
    File.AppendAllText(report, JsonSerializer.Serialize(new { pid = Environment.ProcessId,
        executable = Environment.ProcessPath, directory = Environment.CurrentDirectory }) + "\n");
    return 0;
}

if (args.Length == 1 && args[0] == "self-test")
{
    var seed = RandomNumberGenerator.GetBytes(32);
    try
    {
        var key = Ed25519.PublicKeyFromSeed(seed);
        var data = "synthetic release data"u8.ToArray();
        var signer = new Ed25519Signer();
        signer.InitWithNonExpandedPrivateKey(key, seed);
        signer.AddToBuffer(data, 0, data.Length);
        if (!ReleaseVerifier.VerifyBytes(Convert.ToBase64String(signer.GenerateSignature()), data, Convert.ToBase64String(key)))
            throw new InvalidOperationException("Valid signature was rejected.");
        Console.WriteLine("PASS valid signature");
        return 0;
    }
    finally { CryptographicOperations.ZeroMemory(seed); }
}
if (args.Length == 4 && args[0] == "self-test")
    return ReleaseSelfTests.Run(args[1], args[2], args[3]);
if (args.Length == 5 && args[0] == "generator-test")
    return GeneratorTests.Run(args[1], args[2], args[3], args[4]);
if (args.Length == 5 && args[0] is "seal" or "verify")
{
    var key = File.ReadAllText(args[4]).Trim();
    if (args[0] == "seal") ReleaseVerifier.Seal(args[1], args[2], args[3], key);
    else ReleaseVerifier.Validate(args[1], args[2], args[3], key, true);
    Console.WriteLine("PASS exact release manifest, metadata, Ed25519 installer/feed signatures and checksums");
    return 0;
}
throw new ArgumentException("Unknown release command.");

internal static partial class ReleaseVerifier
{
    internal static bool VerifyBytes(string signature, byte[] data, string key)
    {
        try
        {
            var verifier = new NetSparkleUpdater.SignatureVerifiers.Ed25519Checker(
                NetSparkleUpdater.Enums.SecurityMode.Strict, key, publicKeyFile: null);
            return verifier.VerifySignature(signature, data) == NetSparkleUpdater.Enums.ValidationResult.Valid;
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
