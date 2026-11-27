namespace TjClient.Core;

public sealed record LocalFile(string RelativePath, long Size, long ModifiedTicks, long CreatedTicks);

public static class LogScanner
{
    public static IReadOnlyList<LocalFile> Scan(string root, int? limit = null)
    {
        if (limit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new List<LocalFile>();
        foreach (var file in Enumerate(root))
        {
            if (IsMarkerOnly(root, file)) continue;
            if (limit.HasValue && result.Count >= limit.Value) throw new QueueFullException();
            result.Add(file);
        }
        return result;
    }

    internal static bool IsMarkerOnly(string root, LocalFile file)
    {
        if (file.Size != 3) return false;
        using var log = new LockedLog(root, file, growing: true);
        return log.StartsWithUtf8Marker();
    }

    internal static IEnumerable<LocalFile> Enumerate(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Каталог не найден или недоступен.");
        return Walk(Path.GetFullPath(root), 0);

        IEnumerable<LocalFile> Walk(string directory, int depth)
        {
            if (depth > 8) throw new IOException("Превышена глубина каталога (8).");
            EnsureNoReparse(directory);
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!IsLogPath(relative)) continue;
                EnsureNoReparse(path);
                var info = new FileInfo(path);
                if (info.Length == 0) continue;
                yield return new(relative, info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
                foreach (var file in Walk(child, depth + 1)) yield return file;
        }
    }

    public static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Ссылки и точки повторного анализа в исходном пути запрещены.");
    }

    public static bool IsLogPath(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 2 || segments.Length > 10 || segments.Any(s => s.Length == 0 || s is "." or ".."
            || s.Any(c => char.IsControl(c) || "\\:< >\"|?*".Replace(" ", "").Contains(c)) || s.EndsWith('.') || s.EndsWith(' '))) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(segments[^2], @"^.+_[0-9]+$")) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(segments[^1], @"^[0-9]{8}\.log$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        var name = segments[^1];
        return DateTime.TryParseExact("20" + name[..8], "yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }
}
