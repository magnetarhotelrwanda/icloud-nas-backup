using Microsoft.Data.Sqlite;

namespace ICloudBackup.Core;

public enum FileStatus { Pending = 0, Ok = 1, Failed = 2, Deleted = 3 }

public sealed class FileRecord
{
    public string Job { get; init; } = "";
    public string Rel { get; init; } = "";
    public long Size { get; init; }
    public long Mtime { get; init; }
    public string? Hash { get; init; }
    public FileStatus Status { get; init; }
    public int Attempts { get; init; }
    public long? NextAttempt { get; init; }
    public string? LastError { get; init; }
    public long? BackedUpAt { get; init; }
    public long? VerifiedAt { get; init; }
}

public sealed record JobCounts(int Ok, int Failed, int Deleted, int Pending);

/// <summary>
/// Persistent record of every file backed up: size, modified time and SHA-256 of the verified NAS copy.
/// One connection, serialized with a lock - SQLite is far faster than the NAS so this is never the bottleneck.
/// </summary>
public sealed class StateDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public StateDb(string path)
    {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Private }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS files(
              job TEXT NOT NULL,
              rel TEXT NOT NULL COLLATE NOCASE,
              size INTEGER NOT NULL DEFAULT 0,
              mtime INTEGER NOT NULL DEFAULT 0,
              hash TEXT,
              status INTEGER NOT NULL DEFAULT 0,
              attempts INTEGER NOT NULL DEFAULT 0,
              next_attempt INTEGER,
              last_error TEXT,
              backed_up_at INTEGER,
              verified_at INTEGER,
              PRIMARY KEY(job, rel)
            );
            CREATE INDEX IF NOT EXISTS ix_files_status ON files(status, next_attempt);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            """);
    }

    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Cmd(string sql, params (string, object?)[] args)
    {
        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private const string Columns = "job, rel, size, mtime, hash, status, attempts, next_attempt, last_error, backed_up_at, verified_at";

    private static FileRecord Read(SqliteDataReader r) => new()
    {
        Job = r.GetString(0),
        Rel = r.GetString(1),
        Size = r.GetInt64(2),
        Mtime = r.GetInt64(3),
        Hash = r.IsDBNull(4) ? null : r.GetString(4),
        Status = (FileStatus)r.GetInt32(5),
        Attempts = r.GetInt32(6),
        NextAttempt = r.IsDBNull(7) ? null : r.GetInt64(7),
        LastError = r.IsDBNull(8) ? null : r.GetString(8),
        BackedUpAt = r.IsDBNull(9) ? null : r.GetInt64(9),
        VerifiedAt = r.IsDBNull(10) ? null : r.GetInt64(10),
    };

    private List<FileRecord> Query(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<FileRecord>();
            while (r.Read()) list.Add(Read(r));
            return list;
        }
    }

    public FileRecord? Get(string job, string rel) =>
        Query($"SELECT {Columns} FROM files WHERE job=$j AND rel=$r", ("$j", job), ("$r", rel)).FirstOrDefault();

    public Dictionary<string, FileRecord> GetAll(string job)
    {
        // TryAdd: SQLite's NOCASE only folds ASCII, so two names differing only in accented-letter case can both exist.
        var dict = new Dictionary<string, FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Query($"SELECT {Columns} FROM files WHERE job=$j", ("$j", job))) dict.TryAdd(f.Rel, f);
        return dict;
    }

    public List<FileRecord> GetDueRetries(long now) =>
        Query($"SELECT {Columns} FROM files WHERE status=2 AND next_attempt IS NOT NULL AND next_attempt <= $n", ("$n", now));

    public List<FileRecord> GetFailed() =>
        Query($"SELECT {Columns} FROM files WHERE status=2 ORDER BY attempts DESC, rel");

    public void MarkOk(string job, string rel, long size, long mtime, string hash, bool copied)
    {
        lock (_lock)
        {
            var now = Now;
            using var cmd = Cmd("""
                INSERT INTO files(job, rel, size, mtime, hash, status, attempts, next_attempt, last_error, backed_up_at, verified_at)
                VALUES($j, $r, $s, $m, $h, 1, 0, NULL, NULL, $now, $now)
                ON CONFLICT(job, rel) DO UPDATE SET
                  rel=excluded.rel, size=excluded.size, mtime=excluded.mtime, hash=excluded.hash, status=1,
                  attempts=0, next_attempt=NULL, last_error=NULL,
                  backed_up_at=CASE WHEN $copied=1 OR files.backed_up_at IS NULL THEN excluded.backed_up_at ELSE files.backed_up_at END,
                  verified_at=excluded.verified_at
                """, ("$j", job), ("$r", rel), ("$s", size), ("$m", mtime), ("$h", hash), ("$now", now), ("$copied", copied ? 1 : 0));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Records a failure and returns the new attempt count.</summary>
    public int MarkFailed(string job, string rel, long size, long mtime, string error, long? nextAttempt, bool countAttempt = true)
    {
        lock (_lock)
        {
            using var cmd = Cmd("""
                INSERT INTO files(job, rel, size, mtime, status, attempts, next_attempt, last_error)
                VALUES($j, $r, $s, $m, 2, $inc, $n, $e)
                ON CONFLICT(job, rel) DO UPDATE SET
                  status=2, attempts=files.attempts + $inc, next_attempt=$n, last_error=$e
                RETURNING attempts
                """, ("$j", job), ("$r", rel), ("$s", size), ("$m", mtime), ("$inc", countAttempt ? 1 : 0), ("$n", nextAttempt), ("$e", error));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void MarkDeleted(string job, string rel)
    {
        lock (_lock)
        {
            using var cmd = Cmd("UPDATE files SET status=3, next_attempt=NULL WHERE job=$j AND rel=$r", ("$j", job), ("$r", rel));
            cmd.ExecuteNonQuery();
        }
    }

    public void RetryNow(string job, string rel)
    {
        lock (_lock)
        {
            using var cmd = Cmd("UPDATE files SET next_attempt=$n WHERE job=$j AND rel=$r AND status=2", ("$n", Now), ("$j", job), ("$r", rel));
            cmd.ExecuteNonQuery();
        }
    }

    public int RetryAllNow()
    {
        lock (_lock)
        {
            using var cmd = Cmd("UPDATE files SET next_attempt=$n WHERE status=2", ("$n", Now));
            return cmd.ExecuteNonQuery();
        }
    }

    public void DeleteJob(string job)
    {
        lock (_lock)
        {
            using var cmd = Cmd("DELETE FROM files WHERE job=$j", ("$j", job));
            cmd.ExecuteNonQuery();
        }
    }

    public JobCounts Counts(string job)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT status, COUNT(*) FROM files WHERE job=$j GROUP BY status", ("$j", job));
            using var r = cmd.ExecuteReader();
            int ok = 0, failed = 0, deleted = 0, pending = 0;
            while (r.Read())
            {
                var n = r.GetInt32(1);
                switch ((FileStatus)r.GetInt32(0))
                {
                    case FileStatus.Ok: ok = n; break;
                    case FileStatus.Failed: failed = n; break;
                    case FileStatus.Deleted: deleted = n; break;
                    default: pending = n; break;
                }
            }
            return new JobCounts(ok, failed, deleted, pending);
        }
    }

    public (int Count, long Bytes) CopiedSince(string job, long since)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT COUNT(*), COALESCE(SUM(size),0) FROM files WHERE job=$j AND status=1 AND backed_up_at >= $s",
                ("$j", job), ("$s", since));
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt64(1));
        }
    }

    public string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT value FROM meta WHERE key=$k", ("$k", key));
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = Cmd("INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                ("$k", key), ("$v", value));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock) _conn.Dispose();
    }
}
