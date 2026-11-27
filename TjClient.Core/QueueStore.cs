using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TjClient.Core;

public enum InitialMode { NewFiles, ModifiedSince }
public enum QueueState { Baseline, Pending, Uploading, Retry, Delivered, Importing, Imported, ImportFailed, Expired, Conflict, Error, ImportStatusUnavailable }
public sealed record QueueItem(LocalFile File, string Hash = "", string? UploadId = null, long Offset = 0, QueueState State = QueueState.Pending, long? JobId = null, int Attempts = 0, long NextAttemptTicks = 0, string Error = "", long? RangeStart = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string UploadPath
    {
        get
        {
            if (RangeStart is not long start) return File.RelativePath;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(File.RelativePath.ToUpperInvariant()));
            var parts = File.RelativePath.Split('/');
            return $"tjparts/{Convert.ToHexStringLower(hash)}-{start:x}-{start + File.Size:x}/{parts[^2]}/{parts[^1]}";
        }
    }
}
public sealed class QueueFullException() : IOException("Очередь заполнена. Предел - 10000 передач.");

public sealed class QueueStore : IDisposable
{
    private const int PageBytes = 64 * 1024;
    private readonly SqliteConnection db;
    private readonly int quota;
    // До Cut лежит история, пропущенная при старте. После Position начинаются новые события.
    private sealed record Cursor(long Created, long Position, long Cut, long History, bool Backfill, bool Aligned);

    public QueueStore(string path, int quota = 10000)
    {
        if (quota is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(quota));
        this.quota = quota;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY COLLATE NOCASE, metadata TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cursors(path TEXT PRIMARY KEY COLLATE NOCASE, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS payload(path TEXT COLLATE NOCASE, page INTEGER, data BLOB NOT NULL, PRIMARY KEY(path,page)) WITHOUT ROWID;
            """);
        if (Initialized && Setting("journal_cursors") == null)
        {
            using var transaction = db.BeginTransaction();
            foreach (var item in All())
            {
                bool skipped = item.State == QueueState.Baseline;
                PutCursor(item.File.RelativePath, new(item.File.CreatedTicks, item.File.Size, skipped ? item.File.Size : 0, 0, false, !skipped));
                if (skipped || item.State == QueueState.Imported) Remove(item);
            }
            Set("journal_cursors", "1");
            transaction.Commit();
        }
    }

    public void Bind(string root, string endpoint, long sourceId)
    {
        var identity = new ClientProfile { Folder = root, Endpoint = endpoint, SourceId = sourceId }.QueueKey;
        using var transaction = db.BeginTransaction();
        var stored = Setting("identity");
        if (stored != null && stored != identity) throw new InvalidOperationException("Очередь относится к другой папке или получателю. Нужна отдельная подтверждённая очередь.");
        Set("identity", identity);
        transaction.Commit();
    }
    public bool Initialized => Setting("initialized") != null;

    public IReadOnlyList<QueueItem> All()
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT metadata FROM files ORDER BY path";
        using var rows = cmd.ExecuteReader();
        var result = new List<QueueItem>();
        while (rows.Read())
        {
            var item = JsonSerializer.Deserialize<QueueItem>(rows.GetString(0))!;
            if (item.JobId != null && (item.State == QueueState.Expired
                || (item.State == QueueState.Delivered && item.UploadId == null)))
                item = item with { State = QueueState.ImportStatusUnavailable, Offset = item.File.Size,
                    Error = "Файл принят. Проверьте результат импорта на сайте." };
            result.Add(item);
        }
        return result;
    }
    public void BeginObservation(string root, InitialMode mode, DateTimeOffset? since)
    {
        if (!Initialized) ConfirmObservation(root, mode, since);
    }
    public void ConfirmObservation(string root, InitialMode mode, DateTimeOffset? since)
    {
        long threshold = Threshold(mode, since);
        // Для уже открытых файлов сохраняем границу наблюдения, даже если в файле пока только метка.
        var files = LogScanner.Enumerate(root).ToList();
        using var transaction = db.BeginTransaction();
        bool continueNew = Initialized && mode == InitialMode.NewFiles && DiscoverySince == 0;
        foreach (var file in files)
        {
            var cursor = GetCursor(file.RelativePath);
            bool selected = mode == InitialMode.ModifiedSince && file.ModifiedTicks >= threshold;
            if (cursor == null) PutCursor(file.RelativePath, InitialCursor(root, file, selected || continueNew));
            else if (selected && cursor.History < cursor.Cut)
                PutCursor(file.RelativePath, cursor with { Backfill = true });
        }
        Set("discovery_since_ticks", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set("initialized", "1");
        Set("journal_cursors", "1");
        transaction.Commit();
    }
    public IReadOnlyList<QueueItem> PreviewPeriod(IReadOnlyList<LocalFile> files, InitialMode mode, DateTimeOffset? since)
    {
        long threshold = Threshold(mode, since);
        var preview = All().ToList();
        bool continueNew = Initialized && mode == InitialMode.NewFiles && DiscoverySince == 0;
        foreach (var file in files)
        {
            var cursor = GetCursor(file.RelativePath);
            bool selected = mode == InitialMode.ModifiedSince && file.ModifiedTicks >= threshold;
            if (cursor == null || cursor.History < cursor.Cut || file.Size > cursor.Position)
                preview.Add(new(file, State: selected || (cursor == null ? continueNew : cursor.Backfill || file.Size > cursor.Position)
                    ? QueueState.Pending : QueueState.Baseline));
        }
        return preview;
    }
    private static long Threshold(InitialMode mode, DateTimeOffset? since) => mode == InitialMode.NewFiles ? 0 :
        since?.UtcTicks ?? throw new ArgumentException("Выберите дату начала.");
    private long DiscoverySince => Convert.ToInt64(Setting("discovery_since_ticks"));

    private static Cursor InitialCursor(string root, LocalFile file, bool selected)
    {
        if (LogScanner.IsMarkerOnly(root, file)) return new(file.CreatedTicks, 3, 0, 0, false, true);
        return new(file.CreatedTicks, selected ? 0 : file.Size, selected ? 0 : file.Size, 0, false, selected || file.Size == 0);
    }

    internal long BatchLimit => long.TryParse(Setting("batch_limit"), out long bytes) ? bytes : 1;
    internal void RememberBatchLimit(long bytes)
    {
        if (bytes != BatchLimit) Set("batch_limit", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal bool Capture(string root, long targetBytes, CancellationToken ct)
    {
        if (!Initialized) throw new InvalidOperationException("Нужно подтвердить начальный режим.");
        long threshold = DiscoverySince;
        int available = quota - Count();
        if (available <= 0) return false;
        foreach (var file in LogScanner.Enumerate(root))
        {
            ct.ThrowIfCancellationRequested();
            var cursor = GetCursor(file.RelativePath);
            if (cursor == null)
            {
                cursor = InitialCursor(root, file, file.ModifiedTicks >= threshold);
                PutCursor(file.RelativePath, cursor);
            }
            if (cursor.Created != file.CreatedTicks || cursor.Position > file.Size) throw new FileChangedException();
            bool history = cursor.Backfill && cursor.History < cursor.Cut;
            if (!history && cursor.Position == file.Size) continue;
            if (available <= 0) return false;
            using var log = new LockedLog(root, file, growing: true);
            bool markerAtBoundary = cursor.Cut is > 0 and <= 3 && cursor.History < cursor.Cut && file.Size >= 3;
            if ((markerAtBoundary || (cursor.Position == 0 && file.Size == 3)) && log.StartsWithUtf8Marker())
            {
                cursor = cursor with { History = cursor.Cut, Backfill = false, Aligned = true, Position = Math.Max(3, cursor.Position) };
                PutCursor(file.RelativePath, cursor);
                history = false;
            }
            if (history)
            {
                long start = cursor.History;
                foreach (long end in BatchEnds(log.Stream, start, file.Size, cursor.Cut, targetBytes, ct))
                {
                    bool finished = end >= cursor.Cut;
                    cursor = cursor with { History = end, Backfill = !finished,
                        Aligned = finished || cursor.Aligned, Position = Math.Max(cursor.Position, end) };
                    Stage(file, log, start, end, cursor, ct);
                    start = end;
                    if (--available == 0) return false;
                }
                if (cursor.Backfill) continue;
            }
            if (cursor.Position == file.Size) continue;
            if (!cursor.Aligned)
            {
                // Если наблюдение началось посреди записи, пропускаем её до первого полного разделителя.
                long end = JournalRecords.Ends(log.Stream, 0, file.Size, ct).FirstOrDefault(x => x >= cursor.Cut);
                if (end == 0) continue;
                cursor = cursor with { Aligned = true, Position = end };
                PutCursor(file.RelativePath, cursor);
            }
            long position = cursor.Position;
            foreach (long end in BatchEnds(log.Stream, position, file.Size, file.Size, targetBytes, ct))
            {
                cursor = cursor with { Position = end };
                Stage(file, log, position, end, cursor, ct);
                position = end;
                if (--available == 0) return false;
            }
        }
        return true;
    }
    private static IEnumerable<long> BatchEnds(FileStream stream, long start, long size, long stopAfter, long target, CancellationToken ct)
    {
        long previous = start;
        foreach (long end in JournalRecords.Ends(stream, start, size, ct))
        {
            if (end - start > target && previous > start)
            {
                yield return previous;
                start = previous;
            }
            previous = end;
            if (end >= stopAfter || end - start >= target)
            {
                yield return end;
                start = end;
            }
            if (end >= stopAfter) break;
        }
        if (previous > start) yield return previous;
    }
    private void Stage(LocalFile source, LockedLog log, long start, long end, Cursor cursor, CancellationToken ct)
    {
        var item = new QueueItem(source with { Size = end - start }, RangeStart: start);
        string key = item.UploadPath;
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO payload(path,page,data) VALUES ($path,$page,$data)";
        cmd.Parameters.AddWithValue("$path", key);
        var page = cmd.Parameters.Add("$page", SqliteType.Integer);
        var data = cmd.Parameters.Add("$data", SqliteType.Blob);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[(int)Math.Min(PageBytes, end - start)];
        for (long offset = start; offset < end;)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, end - offset);
            int copied = 0;
            while (copied < count)
            {
                int n = RandomAccess.Read(log.Stream.SafeFileHandle, buffer.AsSpan(copied, count - copied), offset + copied);
                if (n == 0) throw new FileChangedException();
                copied += n;
            }
            hash.AppendData(buffer, 0, count);
            page.Value = (offset - start) / PageBytes;
            data.Value = count == buffer.Length ? buffer : buffer[..count];
            cmd.ExecuteNonQuery();
            offset += count;
        }
        log.Verify();
        Save(item with { Hash = Convert.ToHexStringLower(hash.GetHashAndReset()) });
        PutCursor(source.RelativePath, cursor);
        tx.Commit();
    }

    internal byte[] ReadPayload(string path, long offset, int count)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT page,data FROM payload WHERE path=$p AND page BETWEEN $first AND $last ORDER BY page";
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$first", offset / PageBytes);
        cmd.Parameters.AddWithValue("$last", (offset + count - 1) / PageBytes);
        using var rows = cmd.ExecuteReader();
        var bytes = new byte[count];
        int copied = 0;
        while (copied < count && rows.Read())
        {
            long position = offset + copied;
            if (rows.GetInt64(0) != position / PageBytes) break;
            int skip = (int)(position % PageBytes);
            copied += (int)rows.GetBytes(1, skip, bytes, copied, Math.Min(count - copied, PageBytes - skip));
        }
        if (copied != count) throw new InvalidDataException("В локальной очереди отсутствуют байты порции.");
        return bytes;
    }
    public void Save(QueueItem item)
    {
        if (item.State == QueueState.Imported)
        {
            Discard(item);
            return;
        }
        Upsert("INSERT INTO files(path,metadata) VALUES($p,$v) ON CONFLICT(path) DO UPDATE SET metadata=excluded.metadata", item.UploadPath, JsonSerializer.Serialize(item));
    }
    internal void Discard(QueueItem item)
    {
        using var tx = db.BeginTransaction();
        Remove(item);
        tx.Commit();
    }
    private void Remove(QueueItem item)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM payload WHERE path=$p; DELETE FROM files WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", item.UploadPath);
        cmd.ExecuteNonQuery();
    }
    private int Count() { using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM files"; return Convert.ToInt32(cmd.ExecuteScalar()); }
    private Cursor? GetCursor(string path) => Lookup("SELECT data FROM cursors WHERE path=$p", path) is string json ? JsonSerializer.Deserialize<Cursor>(json) : null;
    private void PutCursor(string path, Cursor cursor) =>
        Upsert("INSERT INTO cursors(path,data) VALUES($p,$v) ON CONFLICT(path) DO UPDATE SET data=excluded.data", path, JsonSerializer.Serialize(cursor));
    private string? Setting(string key) => Lookup("SELECT value FROM settings WHERE key=$p", key);
    private void Set(string key, string value) =>
        Upsert("INSERT INTO settings(key,value) VALUES($p,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    private string? Lookup(string sql, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", key);
        return cmd.ExecuteScalar() as string;
    }
    private void Upsert(string sql, string key, string value)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
    private void Execute(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public void Dispose() => db.Dispose();
}
