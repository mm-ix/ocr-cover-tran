using Microsoft.Data.Sqlite;

namespace ShotTrans.History;

public sealed record HistoryEntry(
    long Id,
    DateTime CreatedAt,
    string Source,
    byte[]? Thumbnail,
    string Original,
    string Translated);

public sealed class HistoryStore
{
    private const int MaxEntries = 200;
    private readonly string _dbPath;
    private readonly object _gate = new();
    private bool _schemaReady;

    public string DatabasePath => _dbPath;

    public HistoryStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        var conn = new SqliteConnection(builder.ToString());
        conn.Open();
        EnsureSchema(conn);
        return conn;
    }

    private void EnsureSchema(SqliteConnection conn)
    {
        if (_schemaReady) return;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_utc_ms INTEGER NOT NULL,
                    source TEXT NOT NULL DEFAULT 'screenshot',
                    thumbnail BLOB NULL,
                    original TEXT NOT NULL,
                    translated TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_history_created ON history(created_utc_ms DESC);
                """;
            cmd.ExecuteNonQuery();
        }
        _schemaReady = true;
    }

    public void Add(string source, byte[]? thumbnail, string original, string translated)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
                using var conn = Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        INSERT INTO history(created_utc_ms, source, thumbnail, original, translated)
                        VALUES ($t, $s, $thumb, $o, $tr);
                        """;
                    cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    cmd.Parameters.AddWithValue("$s", source ?? "screenshot");
                    cmd.Parameters.AddWithValue("$thumb", (object?)thumbnail ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$o", original ?? "");
                    cmd.Parameters.AddWithValue("$tr", translated ?? "");
                    cmd.ExecuteNonQuery();
                }
                using (var trim = conn.CreateCommand())
                {
                    trim.CommandText = """
                        DELETE FROM history WHERE id NOT IN (
                            SELECT id FROM history ORDER BY id DESC LIMIT $max
                        );
                        """;
                    trim.Parameters.AddWithValue("$max", MaxEntries);
                    trim.ExecuteNonQuery();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("写入历史记录失败", ex);
        }
    }

    public List<HistoryEntry> List(int limit = MaxEntries)
    {
        var list = new List<HistoryEntry>();
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_dbPath)) return list;
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id, created_utc_ms, source, thumbnail, original, translated
                    FROM history ORDER BY id DESC LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxEntries));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    byte[]? thumb = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3);
                    list.Add(new HistoryEntry(
                        reader.GetInt64(0),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).LocalDateTime,
                        reader.GetString(2),
                        thumb,
                        reader.GetString(4),
                        reader.GetString(5)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取历史记录失败", ex);
        }
        return list;
    }

    public void Delete(long id)
    {
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM history WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Log.Error("删除历史记录失败", ex);
        }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM history;";
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Log.Error("清空历史记录失败", ex);
        }
    }
}
