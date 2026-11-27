using TjClient.Core;

static class ScannerTests
{
    public static void SafeRead()
    {
        using var fixture = new Fixture();
        var path = fixture.File("rphost_1/26112501.log", "synthetic");
        var file = LogScanner.Scan(fixture.Root).Single();
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            using (var read = new LockedLog(fixture.Root, file, growing: true)) read.Verify();
        using (var read = new LockedLog(fixture.Root, file))
        {
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic"))), read.Hash());
            read.Verify();
            using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer.WriteByte(10);
            writer.Flush();
            read.Verify();
        }
        using var outside = new Fixture();
        outside.File("rphost_1/26112502.log", "outside");
        var link = Path.Combine(fixture.Root, "junction");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, outside.Root }) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<IOException>(() => LogScanner.Scan(fixture.Root)); }
        finally { Directory.Delete(link); }
    }

    public static void ValidPaths()
    {
        using var fixture = new Fixture();
        fixture.File("srv/rphost_1234/26112501.log", "synthetic");
        fixture.File("srv/rphost_1234/26113201.log", "bad date");
        fixture.File("srv/notprocess/26112501.log", "bad directory");
        fixture.File("srv/rphost_1234/26112502.tmp", "unpublished");
        var files = LogScanner.Scan(fixture.Root);
        Assert.Equal(1, files.Count);
        Assert.Equal("srv/rphost_1234/26112501.log", files[0].RelativePath);
        Assert.Equal(9L, files[0].Size);
        foreach (var invalid in new[] { "../rphost_1/26112501.log", "/rphost_1/26112501.log", "rphost_1\\26112501.log", "rphost_1/26112501.log:x", "rphost_1//26112501.log", "rphost_1/26112599.log" })
            Assert.True(!LogScanner.IsLogPath(invalid));
        Assert.Throws<DirectoryNotFoundException>(() => LogScanner.Scan(Path.Combine(fixture.Root, "missing")));
    }
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "TJCloudClient-tests", Guid.NewGuid().ToString("N"));
    public Fixture() => Directory.CreateDirectory(Root);
    public string File(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); }
}
