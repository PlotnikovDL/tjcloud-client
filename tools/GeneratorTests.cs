using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Chaos.NaCl;

internal static class GeneratorTests
{
    internal static int Run(string generator, string setup, string root, string version)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Generator integration requires a hosted runner because its constructor creates a user directory.");
        if (!Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"))
            throw new InvalidDataException("Invalid release version.");
        if (Directory.Exists(root)) throw new InvalidDataException("Generator fixture must be fresh.");
        Directory.CreateDirectory(root);

        const string repository = "https://example.org/synthetic-client";
        var names = ReleaseVerifier.Names(version);
        File.Copy(setup, Path.Combine(root, names[0]));
        File.WriteAllText(Path.Combine(root, names[1]), "Synthetic ZIP fixture only.");
        var seed = RandomNumberGenerator.GetBytes(32);
        try
        {
            var key = Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed));
            var start = new ProcessStartInfo(generator) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--single-file", Path.Combine(root, names[0]), "--file-version", "9.9.9", "--os", "windows-x64",
                "--base-url", repository + "/releases/download/v" + version, "--appcast-output-directory", root,
                "--product-name", "Synthetic client", "--use-ed25519-signature-attribute" }) start.ArgumentList.Add(arg);
            start.Environment["SPARKLE_PRIVATE_KEY"] = Convert.ToBase64String(seed);
            start.Environment["SPARKLE_PUBLIC_KEY"] = key;
            start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var child = Process.Start(start) ?? throw new InvalidOperationException("Generator did not start.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(120000)) { child.Kill(true); throw new InvalidOperationException("Generator timed out."); }
            Task.WaitAll(output, error);
            if (child.ExitCode != 0) throw new InvalidOperationException("Generator failed. Its output is intentionally not forwarded.");
            ReleaseVerifier.Seal(root, version, repository, key);
            Console.WriteLine("PASS actual AppCastGenerator CLI, ProductVersion precedence, independent signatures and exact checksums");
            Console.WriteLine("RESULT generator-tests=1 failed=0");
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }
}
