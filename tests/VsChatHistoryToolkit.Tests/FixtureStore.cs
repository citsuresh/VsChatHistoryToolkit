using Microsoft.Data.Sqlite;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Tests;

/// <summary>
/// Generates a synthetic Copilot CLI store (schema 8, WAL mode) in a temp folder. Nothing here comes from real chats.
/// </summary>
public sealed class FixtureStore : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Where a store records its schema number: the real store uses a schema_version table and leaves user_version 0.</summary>
    public enum SchemaMarker { Table, UserVersion, None }

    public FixtureStore(int schemaVersion = 8, bool createDb = true, SchemaMarker marker = SchemaMarker.Table)
    {
        Root = Path.Combine(Path.GetTempPath(), "vschat-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        LogDir = Path.Combine(Root, "_log");
        Directory.CreateDirectory(SessionStateDir);
        if (createDb) CreateSchema(schemaVersion, marker);
    }

    public string Root { get; }
    public string LogDir { get; }
    public string DbPath => Path.Combine(Root, "session-store.db");
    public string SessionStateDir => Path.Combine(Root, "session-state");
    public StorePaths Paths => new(Root);

    public SqliteConnection OpenWriter()
    {
        var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        return c;
    }

    public void Exec(string sql, params (string Name, object? Value)[] ps)
    {
        using var c = OpenWriter();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema(int version, SchemaMarker marker)
    {
        using var c = OpenWriter();
        void X(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        X("PRAGMA journal_mode=WAL");
        X("""
            CREATE TABLE sessions(id TEXT PRIMARY KEY, cwd TEXT, repository TEXT, host_type TEXT, branch TEXT,
                                  summary TEXT, created_at TEXT, updated_at TEXT);
            CREATE TABLE turns(id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT, turn_index INTEGER,
                               user_message TEXT, assistant_response TEXT, timestamp TEXT,
                               UNIQUE(session_id, turn_index));
            CREATE TABLE checkpoints(session_id TEXT, checkpoint_number INTEGER, title TEXT, overview TEXT, history TEXT,
                                     work_done TEXT, technical_details TEXT, important_files TEXT, next_steps TEXT,
                                     created_at TEXT);
            CREATE TABLE session_files(id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, file_path TEXT NOT NULL,
                                       tool_name TEXT, turn_index INTEGER, first_seen_at TEXT, UNIQUE(session_id, file_path));
            CREATE TABLE session_refs(session_id TEXT, ref TEXT);
            CREATE TABLE schema_version(version INTEGER NOT NULL);
            CREATE VIRTUAL TABLE search_index USING fts5(content, session_id UNINDEXED, source_type UNINDEXED, source_id UNINDEXED);
            """);
        if (marker == SchemaMarker.Table) X($"INSERT INTO schema_version(version) VALUES({version})");
        if (marker == SchemaMarker.UserVersion) X($"PRAGMA user_version={version}");
    }

    public void AddSession(string id, string? summary = "first message", string? repo = "org/repo", string? cwd = @"C:\src\repo",
        string? branch = "main", string created = "2026-01-05T10:00:00.000Z", string updated = "2026-01-05T10:00:00.000Z")
        => Exec("INSERT INTO sessions(id,cwd,repository,host_type,branch,summary,created_at,updated_at) VALUES($id,$cwd,$repo,'vs',$branch,$s,$c,$u)",
            ("$id", id), ("$cwd", cwd), ("$repo", repo), ("$branch", branch), ("$s", summary), ("$c", created), ("$u", updated));

    public void AddTurn(string sessionId, int index, string user, string assistant, string timestamp, bool index_ = true)
    {
        Exec("INSERT INTO turns(session_id,turn_index,user_message,assistant_response,timestamp) VALUES($s,$i,$u,$a,$t)",
            ("$s", sessionId), ("$i", index), ("$u", user), ("$a", assistant), ("$t", timestamp));
        if (!index_) return;
        using var c = OpenWriter();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM turns WHERE session_id=$s AND turn_index=$i";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$i", index);
        var turnId = Convert.ToString(cmd.ExecuteScalar());
        AddSearchRow(user + " " + assistant, "turn", turnId!, sessionId);
    }

    public void AddCheckpoint(string sessionId, int number, string title, string overview = "overview", string? history = null,
        string? workDone = null, string? technical = null, string? files = null, string? next = null)
    {
        Exec("INSERT INTO checkpoints VALUES($s,$n,$t,$o,$h,$w,$d,$f,$x,'2026-01-05T10:00:00.000Z')",
            ("$s", sessionId), ("$n", number), ("$t", title), ("$o", overview), ("$h", history), ("$w", workDone), ("$d", technical), ("$f", files), ("$x", next));
        AddSearchRow(title + " " + overview, "checkpoint_overview", number.ToString(), sessionId);
    }

    /// <summary>Creates the per-request usage table with the columns of the real store (synthetic rows only).</summary>
    public void AddUsageTable(bool dropColumn = false, bool withId = true) => Exec($"""
        CREATE TABLE assistant_usage_events({(withId ? "id INTEGER PRIMARY KEY AUTOINCREMENT, " : "")}session_id TEXT NOT NULL, turn_index INTEGER NOT NULL,
            agent_id TEXT, parent_tool_call_id TEXT, model TEXT NOT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
            cache_read_tokens INTEGER NOT NULL, cache_write_tokens INTEGER NOT NULL, reasoning_tokens INTEGER,
            {(dropColumn ? "" : "total_nano_aiu INTEGER,")} request_multiplier REAL, duration_ms INTEGER NOT NULL, created_at TEXT NOT NULL)
        """);

    public void AddUsage(string sessionId, int turn, string model, long input, long output, long cacheRead = 0, long cacheWrite = 0,
        long? reasoning = 0, long? nanoAiu = 0, long durationMs = 100, string createdAt = "2026-01-05T10:06:00.000Z", string? agentId = null)
        => Exec("INSERT INTO assistant_usage_events(session_id,turn_index,agent_id,model,input_tokens,output_tokens,cache_read_tokens,cache_write_tokens,reasoning_tokens,total_nano_aiu,duration_ms,created_at) " +
                "VALUES($s,$t,$a,$m,$i,$o,$cr,$cw,$r,$n,$d,$c)",
            ("$s", sessionId), ("$t", turn), ("$a", agentId), ("$m", model), ("$i", input), ("$o", output), ("$cr", cacheRead),
            ("$cw", cacheWrite), ("$r", reasoning), ("$n", nanoAiu), ("$d", durationMs), ("$c", createdAt));

    public void AddFile(string sessionId, string filePath, string? tool = "edit", int? turn = 0, string? firstSeen = "2026-01-05T10:06:00.000Z")
        => Exec("INSERT INTO session_files(session_id,file_path,tool_name,turn_index,first_seen_at) VALUES($s,$p,$t,$i,$f)",
            ("$s", sessionId), ("$p", filePath), ("$t", tool), ("$i", turn), ("$f", firstSeen));

    public void AddSearchRow(string content, string sourceType, string sourceId, string? sessionId = null) =>
        Exec("INSERT INTO search_index(content,session_id,source_type,source_id) VALUES($c,$s,$t,$i)",
            ("$c", content), ("$s", sessionId), ("$t", sourceType), ("$i", sourceId));

    public string SessionDir(string id) => Path.Combine(SessionStateDir, id);
    public string YamlPath(string id) => Path.Combine(SessionDir(id), "workspace.yaml");
    public string EventsPath(string id) => Path.Combine(SessionDir(id), "events.jsonl");
    public string LogPath => Path.Combine(LogDir, "rename-log.jsonl");

    /// <summary>Writes workspace.yaml with exact bytes and an old modification time so the session reads as idle.</summary>
    public void WriteYaml(string id, byte[] bytes, bool old = true)
    {
        Directory.CreateDirectory(SessionDir(id));
        File.WriteAllBytes(YamlPath(id), bytes);
        if (old) File.SetLastWriteTimeUtc(YamlPath(id), Now.UtcDateTime.AddDays(-2));
    }

    public void WriteYaml(string id, string text, bool old = true) => WriteYaml(id, new System.Text.UTF8Encoding(false).GetBytes(text), old);

    public void WriteEvents(string id, IEnumerable<string> lines, bool old = true)
    {
        Directory.CreateDirectory(SessionDir(id));
        File.WriteAllLines(EventsPath(id), lines);
        if (old) File.SetLastWriteTimeUtc(EventsPath(id), Now.UtcDateTime.AddDays(-2));
    }

    /// <summary>Writes events.jsonl exactly as given (any line endings, no trailing newline added).</summary>
    public void WriteEventsRaw(string id, string text, bool old = true)
    {
        Directory.CreateDirectory(SessionDir(id));
        File.WriteAllText(EventsPath(id), text, new System.Text.UTF8Encoding(false));
        if (old) File.SetLastWriteTimeUtc(EventsPath(id), Now.UtcDateTime.AddDays(-2));
    }

    public static string SampleYaml(string id, string? name = "Original title", bool userNamed = false) =>
        $"id: {id}\ncwd: C:\\src\\repo\ngit_root: C:\\src\\repo\nrepository: org/repo\nhost_type: vs\nbranch: main\nclient_name: vs\n"
        + (name is null ? "" : $"name: {name}\n")
        + $"user_named: {(userNamed ? "true" : "false")}\nsummary_count: 0\nfork_count: 0\ncreated_at: 2026-01-05T10:00:00.000Z\nupdated_at: 2026-01-05T10:00:00.000Z\n";

    /// <summary>A complete idle session: row, old turn, folder with YAML.</summary>
    public string AddIdleSession(string id, string? summary = "first message", string? yamlName = "Original title", bool userNamed = false)
    {
        AddSession(id, summary);
        AddTurn(id, 0, "hello", "hi there", "2026-01-05T10:05:00.000Z");
        WriteYaml(id, SampleYaml(id, yamlName, userNamed));
        return id;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
