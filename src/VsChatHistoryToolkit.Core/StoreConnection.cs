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

    /// <summary>
    /// The real store keeps its schema number in a one-row schema_version table and leaves PRAGMA user_version at 0.
    /// The table is authoritative; user_version is used only when the table does not exist.
    /// </summary>
    private static void EnsureSchema(SqliteConnection conn)
    {
        long? version;
        string source;
        try
        {
            if (Scalar(conn, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'") is not null)
            {
                source = "schema_version table";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_version";
                using var reader = cmd.ExecuteReader();
                reader.Read();
                var rows = reader.GetInt64(0);
                if (rows != 1)
                    throw new ToolkitException(ErrorKind.SchemaMismatch,
                        $"The schema_version table of session-store.db has {rows} rows; exactly one row with version {ExpectedSchemaVersion} is expected.");
                version = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            }
            else
            {
                version = Convert.ToInt64(Scalar(conn, "PRAGMA user_version"));
                source = "PRAGMA user_version";
            }
        }
        catch (SqliteException ex)
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable, $"Cannot read the schema version of session-store.db: {ex.Message}");
        }

        if (version is null)
            throw new ToolkitException(ErrorKind.SchemaMismatch,
                $"The version in the {source} of session-store.db is NULL; this toolkit supports version {ExpectedSchemaVersion} only.");
        if (version != ExpectedSchemaVersion)
            throw new ToolkitException(ErrorKind.SchemaMismatch,
                $"Unsupported session-store.db schema version {version} (from the {source}); this toolkit supports version {ExpectedSchemaVersion} only.");
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }
}