using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Chaos.NaCl;

internal static class ReleaseSelfTests
{
    internal static int Run(string setupPath, string root, string version)
    {
        if (!Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z"))
            throw new InvalidDataException("Invalid release version.");
        Directory.CreateDirectory(root);
        var seed = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToBase64String(Ed25519.PublicKeyFromSeed(seed));

        const string repository = "https://example.org/synthetic-client";
        var setup = $"TJCloudClient-{version}-win-x64-setup.exe";
        var zip = $"TJCloudClient-{version}-win-x64.zip";
        var passed = 0;
        void Check(string name, Action action, bool reject = false)
        {
            try { action(); if (reject) throw new Exception("Expected rejection was missing: " + name); }
            catch (InvalidDataException) when (reject) { }
            Console.WriteLine("PASS " + name);
            passed++;
        }
        string Sign(byte[] bytes)
        {
            var signer = new Ed25519Signer();
            signer.InitWithNonExpandedPrivateKey(Convert.FromBase64String(key), seed);
            signer.AddToBuffer(bytes, 0, bytes.Length);
            return Convert.ToBase64String(signer.GenerateSignature());
        }
        void Feed(Action<XElement>? mutate = null)
        {
            XNamespace ns = "http://www.andymatuschak.org/xml-namespaces/sparkle";
            var enclosure = new XElement("enclosure", new XAttribute("url", repository + "/releases/download/v" + version + "/" + setup),
                new XAttribute("length", new FileInfo(Path.Combine(root, setup)).Length),
                new XAttribute(ns + "version", version), new XAttribute(ns + "os", "windows-x64"),
                new XAttribute(ns + "edSignature", Sign(File.ReadAllBytes(Path.Combine(root, setup)))));
            mutate?.Invoke(enclosure);
            var xml = new XDocument(new XElement("rss", new XElement("channel", new XElement("item", enclosure))));
            File.WriteAllText(Path.Combine(root, "appcast.xml"), xml.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "appcast.xml.signature"), Sign(File.ReadAllBytes(Path.Combine(root, "appcast.xml"))));
        }
        void Verify(string? publicKey = null) => ReleaseVerifier.Validate(root, version, repository, publicKey ?? key, false);
        try
        {
            File.Copy(setupPath, Path.Combine(root, setup));
            File.WriteAllText(Path.Combine(root, zip), "Synthetic ZIP fixture, not for distribution.");
            Feed();
            Check("valid actual Inno installer and detached feed", () => Verify());
            File.AppendAllText(Path.Combine(root, "appcast.xml"), " ");
            Check("tampered feed", () => Verify(), true);
            Feed();
            var original = File.ReadAllBytes(Path.Combine(root, setup));
            var tampered = original.ToArray(); tampered[^1] ^= 1;
            File.WriteAllBytes(Path.Combine(root, setup), tampered);
            Check("tampered installer", () => Verify(), true);
            File.WriteAllBytes(Path.Combine(root, setup), original);
            var wrongKey = Convert.ToBase64String(Ed25519.PublicKeyFromSeed(RandomNumberGenerator.GetBytes(32)));
            Check("wrong public key", () => Verify(wrongKey), true);
            Check("missing public key", () => Verify(""), true);
            Check("malformed public key", () => Verify("not-base64"), true);
            XNamespace ns = "http://www.andymatuschak.org/xml-namespaces/sparkle";
            foreach (var mutation in new (string Name, Action<XElement> Apply)[] {
                ("wrong version", e => e.SetAttributeValue(ns + "version", "v" + version)),
                ("wrong URL", e => e.SetAttributeValue("url", "https://example.org/wrong.exe")),
                ("wrong size", e => e.SetAttributeValue("length", "1")),
                ("wrong architecture", e => e.SetAttributeValue(ns + "os", "windows-arm64")),
                ("missing installer signature", e => e.Attribute(ns + "edSignature")!.Remove()) })
            {
                Feed(mutation.Apply);
                Check(mutation.Name, () => Verify(), true);
            }
            Feed();
            var multiple = XDocument.Load(Path.Combine(root, "appcast.xml"));
            var extra = new XElement(multiple.Root!.Element("channel")!.Element("item")!);
            extra.Element("enclosure")!.SetAttributeValue(ns + "version", "0.9.0");
            multiple.Root.Element("channel")!.Add(extra);
            File.WriteAllText(Path.Combine(root, "appcast.xml"), multiple.ToString());
            File.WriteAllText(Path.Combine(root, "appcast.xml.signature"), Sign(File.ReadAllBytes(Path.Combine(root, "appcast.xml"))));
            Check("additional signed appcast version rejected", () => Verify(), true);
            Feed();
            File.Delete(Path.Combine(root, "appcast.xml.signature"));
            Check("missing detached signature", () => Verify(), true);
            Feed();
            File.WriteAllText(Path.Combine(root, "extra.exe"), "synthetic");
            Check("extra release asset", () => Verify(), true);
            File.Delete(Path.Combine(root, "extra.exe"));
            Check("valid again after rejected input", () => Verify());
            ReleaseVerifier.Seal(root, version, repository, key);
            Check("sealed checksums cover all four payload assets", () => ReleaseVerifier.Validate(root, version, repository, key, true));
            File.AppendAllText(Path.Combine(root, zip), "tampered");
            Check("tampered ZIP rejected by manifest", () => ReleaseVerifier.Validate(root, version, repository, key, true), true);
            File.WriteAllText(Path.Combine(root, zip), "Synthetic ZIP fixture, not for distribution.");
            Check("manifest restored before checksum mutation", () => ReleaseVerifier.Validate(root, version, repository, key, true));
            File.AppendAllText(Path.Combine(root, "SHA256SUMS.txt"), "duplicate entry");
            Check("extra checksum entry rejected", () => ReleaseVerifier.Validate(root, version, repository, key, true), true);
            File.Delete(Path.Combine(root, "SHA256SUMS.txt"));
            ReleaseVerifier.Seal(root, version, repository, key);
            File.WriteAllText(root + ".public-key.txt", key);
            Check("final synthetic fixture is independently verifiable", () => ReleaseVerifier.Validate(root, version, repository, key, true));
            Console.WriteLine($"RESULT release-tests={passed} failed=0");
            return 0;
        }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }
}
