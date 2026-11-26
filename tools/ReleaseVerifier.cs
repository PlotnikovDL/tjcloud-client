using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

internal static partial class ReleaseVerifier
{
    internal static string[] Names(string version) => [
        $"TJCloudClient-{version}-win-x64-setup.exe", $"TJCloudClient-{version}-win-x64.zip",
        "appcast.xml", "appcast.xml.signature"];

    internal static void Validate(string directory, string version, string repository, string key, bool checksums)
    {
        try
        {
            if (!Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") ||
                !Uri.TryCreate(repository, UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
                origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 || repository.EndsWith('/'))
                throw new InvalidDataException("Invalid release identity.");
            if (Convert.FromBase64String(key).Length != 32) throw new InvalidDataException("Invalid public key.");
            var names = Names(version);
            var expected = checksums ? names.Append("SHA256SUMS.txt").ToArray() : names;
            var files = Directory.GetFileSystemEntries(directory);
            if (!expected.Order(StringComparer.Ordinal).SequenceEqual(files.Select(Path.GetFileName).Order(StringComparer.Ordinal)) ||
                files.Any(p => (File.GetAttributes(p) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
                throw new InvalidDataException("Release assets must match the exact manifest.");
            string PathOf(string name) => Path.Combine(directory, name);
            var setup = PathOf(names[0]);
            if (FileVersionInfo.GetVersionInfo(setup).ProductVersion?.Trim() != version)
                throw new InvalidDataException("Installer ProductVersion does not match the numeric release version.");
            var feed = PathOf("appcast.xml");
            if (new FileInfo(feed).Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversized appcast.");
            var checker = new Ed25519Checker(SecurityMode.Strict, key, publicKeyFile: null, readFileBeingVerifiedInChunks: true);
            var detached = File.ReadAllText(PathOf("appcast.xml.signature")).Trim();
            if (Convert.FromBase64String(detached).Length != 64 || checker.VerifySignatureOfFile(detached, feed) != ValidationResult.Valid)
                throw new InvalidDataException("Detached appcast signature is invalid.");
            using var reader = XmlReader.Create(feed, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var xml = XDocument.Load(reader);
            var items = xml.Root?.Element("channel")?.Elements("item").ToArray();
            if (xml.Root?.Name != "rss" || items?.Length != 1 || xml.Descendants("enclosure").Count() != 1)
                throw new InvalidDataException("Appcast must contain exactly one release enclosure.");
            var enclosure = items[0].Element("enclosure") ?? throw new InvalidDataException("Missing enclosure.");
            XNamespace ns = "http://www.andymatuschak.org/xml-namespaces/sparkle";
            if ((string?)enclosure.Attribute(ns + "version") != version ||
                (string?)enclosure.Attribute(ns + "os") != "windows-x64" ||
                (string?)enclosure.Attribute("url") != repository + "/releases/download/v" + version + "/" + names[0] ||
                (string?)enclosure.Attribute("length") != new FileInfo(setup).Length.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Appcast version, URL, architecture or byte size is invalid.");
            var signature = (string?)enclosure.Attribute(ns + "edSignature");
            if (signature is null || Convert.FromBase64String(signature).Length != 64 ||
                checker.VerifySignatureOfFile(signature, setup) != ValidationResult.Valid)
                throw new InvalidDataException("Installer Ed25519 signature is invalid.");
            if (checksums && !File.ReadAllLines(PathOf("SHA256SUMS.txt")).SequenceEqual(ChecksumLines(directory, version)))
                throw new InvalidDataException("Release checksums do not match final bytes.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or XmlException or FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidDataException("Malformed or missing release data.", exception); }
    }

    internal static string[] ChecksumLines(string directory, string version) => Names(version).Select(name =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))) + "  " + name).ToArray();

    internal static void Seal(string directory, string version, string repository, string key)
    {
        Validate(directory, version, repository, key, false);
        File.WriteAllLines(Path.Combine(directory, "SHA256SUMS.txt"), ChecksumLines(directory, version), new UTF8Encoding(false));
        Validate(directory, version, repository, key, true);
    }
}
