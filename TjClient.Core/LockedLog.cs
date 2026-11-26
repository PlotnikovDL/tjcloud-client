namespace TjClient.Core;

public sealed class FileChangedException() : IOException("Файл изменился. Передача остановлена, исходник не заменён.");

public sealed class LockedLog : IDisposable
{
    public FileStream Stream { get; }
    private readonly string path;
    private readonly LocalFile file;
    public LockedLog(string root, LocalFile file)
    {
        if (!LogScanner.IsLogPath(file.RelativePath)) throw new IOException("Недопустимый относительный путь.");
        this.file = file;
        path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        LogScanner.EnsureNoReparse(path);
        Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try { Verify(); } catch { Stream.Dispose(); throw; }
    }
    public string Hash() { Stream.Position = 0; return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Stream)); }
    public void Verify()
    {
        LogScanner.EnsureNoReparse(path);
        var final = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandle(Stream.SafeFileHandle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("Не удалось проверить открытый путь.");
        var resolved = final.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        if (!string.Equals(resolved, path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Открытый путь не совпадает с разрешённым каталогом.");
        var info = new FileInfo(path);
        if (info.Length != file.Size || info.LastWriteTimeUtc.Ticks != file.ModifiedTicks || info.CreationTimeUtc.Ticks != file.CreatedTicks)
            throw new FileChangedException();
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path, uint length, uint flags);
    public void Dispose() => Stream.Dispose();
}
