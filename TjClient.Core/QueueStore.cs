namespace TjClient.Core;

public enum InitialMode { NewFiles, ModifiedSince }
public enum QueueState { Baseline, Pending, Uploading, Retry, Delivered, Importing, Imported, ImportFailed, Expired, Conflict, Error, ImportStatusUnavailable }
public sealed record QueueItem(LocalFile File, string Hash = "", string? UploadId = null, long Offset = 0, QueueState State = QueueState.Pending, long? JobId = null, int Attempts = 0, long NextAttemptTicks = 0, string Error = "");
public sealed class QueueFullException() : IOException("Очередь заполнена. Предел - 10000 файлов, отправка остановлена.");

public sealed class QueueStore : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection db;
    private readonly int quota;
    public QueueStore(string path, int quota = 10000)
    {
        if (quota is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(quota));
        this.quota = quota;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY COLLATE NOCASE, metadata TEXT NOT NULL); CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    }
    public void Bind(string root, string endpoint, long sourceId)
    {
        var identity = new ClientProfile { Folder = root, Endpoint = endpoint, SourceId = sourceId }.QueueKey;
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key='identity'";
        var stored = cmd.ExecuteScalar() as string;
        if (stored != null && stored != identity) throw new InvalidOperationException("Очередь относится к другой папке или получателю. Нужна отдельная подтверждённая очередь.");
        cmd.CommandText = "INSERT OR IGNORE INTO settings(key,value) VALUES ('identity',$identity)";
        cmd.Parameters.AddWithValue("$identity", identity);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }
    public bool Initialized => Scalar("SELECT COUNT(*) FROM settings WHERE key='initialized'") == 1;
    public IReadOnlyList<QueueItem> All()
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT metadata FROM files ORDER BY path";
        using var rows = cmd.ExecuteReader();
        var result = new List<QueueItem>();
        while (rows.Read())
        {
            var item = System.Text.Json.JsonSerializer.Deserialize<QueueItem>(rows.GetString(0))!;
            // Предыдущие версии могли сбросить идентификатор принятой передачи при ручном повторе.
            if (item.JobId != null && (item.State == QueueState.Expired
                || (item.State == QueueState.Delivered && item.UploadId == null)))
                item = item with { State = QueueState.ImportStatusUnavailable, Offset = item.File.Size,
                    Error = "Файл принят. Проверьте результат импорта на сайте." };
            result.Add(item);
        }
        return result;
    }
    public void BeginObservation(string root, IReadOnlyList<LocalFile> preview, InitialMode mode, DateTimeOffset? since)
    {
        if (!Initialized) Initialize(LogScanner.Scan(root), mode, since);
        else SaveDiscoveryPeriod(mode, since);
    }
    public void Initialize(IReadOnlyList<LocalFile> files, InitialMode mode, DateTimeOffset? since)
    {
        if (Initialized) return;
        if (mode == InitialMode.ModifiedSince && since == null) throw new ArgumentException("Выберите дату начала.");
        if (files.Count > quota) throw new QueueFullException();
        using var transaction = db.BeginTransaction();
        foreach (var file in files)
            Save(new(file, State: mode == InitialMode.NewFiles || file.ModifiedTicks < since!.Value.UtcTicks ? QueueState.Baseline : QueueState.Pending));
        SaveDiscoveryPeriod(mode, since);
        Execute("INSERT INTO settings VALUES ('initialized','1')");
        transaction.Commit();
    }
    public void Discover(IReadOnlyList<LocalFile> files)
    {
        if (!Initialized) throw new InvalidOperationException("Нужно подтвердить начальный режим.");
        using var transaction = db.BeginTransaction();
        var known = All().Select(x => x.File.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = files.Where(x => !known.Contains(x.RelativePath)).ToList();
        if (known.Count + fresh.Count > quota) throw new QueueFullException();
        var sinceTicks = DiscoverySinceTicks;
        foreach (var file in fresh) Save(new(file, State: file.ModifiedTicks < sinceTicks ? QueueState.Baseline : QueueState.Pending));
        transaction.Commit();
    }
    public IReadOnlyList<LocalFile> SelectForDiscovery(IReadOnlyList<LocalFile> files)
    {
        var sinceTicks = DiscoverySinceTicks;
        return files.Where(x => x.ModifiedTicks >= sinceTicks).ToList();
    }
    private long DiscoverySinceTicks => Scalar("SELECT COALESCE((SELECT value FROM settings WHERE key='discovery_since_ticks'),0)");
    private void SaveDiscoveryPeriod(InitialMode mode, DateTimeOffset? since)
    {
        if (mode == InitialMode.ModifiedSince && since == null) throw new ArgumentException("Выберите дату начала.");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO settings(key,value) VALUES ('discovery_since_ticks',$since)";
        cmd.Parameters.AddWithValue("$since", mode == InitialMode.ModifiedSince ? since!.Value.UtcTicks : 0);
        cmd.ExecuteNonQuery();
    }
    public void Save(QueueItem item)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO files(path,metadata) VALUES ($path,$data) ON CONFLICT(path) DO UPDATE SET metadata=excluded.metadata";
        cmd.Parameters.AddWithValue("$path", item.File.RelativePath);
        cmd.Parameters.AddWithValue("$data", System.Text.Json.JsonSerializer.Serialize(item));
        cmd.ExecuteNonQuery();
    }
    private long Scalar(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
    private void Execute(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
    public void Dispose() => db.Dispose();
}
