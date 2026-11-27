using Microsoft.Data.Sqlite;
using System.Text.Json;
using TjClient.Core;

static class LegacyQueue
{
    // Реальная схема версии 1.0.3 до первого открытия новой версией.
    public static QueueStore Open(string path, string root, Func<LocalFile, QueueItem>? saved = null,
        IReadOnlyList<LocalFile>? snapshot = null)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE files(path TEXT PRIMARY KEY COLLATE NOCASE, metadata TEXT NOT NULL);
                INSERT INTO settings VALUES('initialized','1'),('discovery_since_ticks','0');
                """;
            cmd.ExecuteNonQuery();
            foreach (var file in snapshot ?? LogScanner.Scan(root))
            {
                cmd.CommandText = "INSERT INTO files VALUES($p,$v)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$p", file.RelativePath);
                var row = saved?.Invoke(file) ?? new QueueItem(file);
                cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(new { row.File, row.Hash, row.UploadId,
                    row.Offset, row.State, row.JobId, row.Attempts, row.NextAttemptTicks, row.Error }));
                cmd.ExecuteNonQuery();
            }
        }
        return new QueueStore(path);
    }
}
