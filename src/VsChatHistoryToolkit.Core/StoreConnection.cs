using Microsoft.Data.Sqlite;

namespace VsChatHistoryToolkit;

/// <summary>
/// Opens session-store.db. Read paths use mode=ro plus query_only (never immutable=1: it ignores the WAL).
/// Only <see cref="OpenReadWrite"/> can write, and it never creates the file.
/// </summary>
public static class StoreConnection
{
    public const int ExpectedSchemaVersion = 8;

    public static SqliteConnection OpenReadOnly(StorePaths paths, int busyTimeoutMs = 3000)
    {
        var conn = Open(paths, SqliteOpenMode.ReadOnly, busyTimeoutMs);
        try
        {
            Exec(conn, "PRAGMA query_only=ON");
            EnsureSchema(conn);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public static SqliteConnection OpenReadWrite(StorePaths paths, int busyTimeoutMs)
    {
        var conn = Open(paths, SqliteOpenMode.ReadWrite, busyTimeoutMs);
        try
        {
            EnsureSchema(conn);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(StorePaths paths, SqliteOpenMode mode, int busyTimeoutMs)
    {
        if (!File.Exists(paths.DbPath))
            throw new ToolkitException(ErrorKind.StoreUnavailable, $"session-store.db not found at '{paths.DbPath}'.");

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DbPath,
            Mode = mode,
            Pooling = false,
            // Microsoft.Data.Sqlite retries busy databases for its own default (30 s) and ignores PRAGMA busy_timeout.
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(busyTimeoutMs / 1000.0)),
        }.ToString();

        var conn = new SqliteConnection(cs);
        try
        {
            conn.Open();
            Exec(conn, $"PRAGMA busy_timeout={busyTimeoutMs}");
            return conn;
        }
        catch (SqliteException ex)
        {
            conn.Dispose();
            throw new ToolkitException(ErrorKind.StoreUnavailable, $"Cannot open session-store.db ({mode}): {ex.Message}");
        }
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        long version;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            version = Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (SqliteException ex)
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable, $"Cannot read the schema version of session-store.db: {ex.Message}");
        }

        if (version != ExpectedSchemaVersion)
            throw new ToolkitException(ErrorKind.SchemaMismatch,
                $"Unsupported session-store.db schema version {version}; this toolkit supports version {ExpectedSchemaVersion} only.");
    }
}
