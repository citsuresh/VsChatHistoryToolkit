using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VsChatHistoryToolkit;

/// <summary>Read-only queries over session-store.db. Every method opens its own read-only connection and returns capped results.</summary>
public sealed class ChatStore
{
    private readonly StorePaths _paths;

    public ChatStore(StorePaths paths) => _paths = paths;

    public sealed record SessionQuery(string? Repo, string? Cwd, string? Branch, string? From, string? To, int? Limit);

    public SearchResult Search(string terms, int? limit)
    {
        var take = Caps.ClampLimit(limit, out var clamped);
        var tokens = FtsQuery.Tokenize(terms ?? "", out var tokensCut);
        if (tokens.Count == 0)
            throw new ToolkitException(ErrorKind.Usage, "Search needs at least one word or number to look for.");

        using var conn = Open();
        var match = FtsQuery.ToMatch(tokens);
        var rows = QuerySnippets(conn, match, take + 1);

        var truncated = clamped || tokensCut || rows.Count > take;
        var hits = new List<SearchHit>();
        foreach (var (snippet, sourceType, sourceId) in rows.Take(take))
        {
            var text = TextUtil.Cap(TextUtil.Collapse(snippet), Caps.SnippetChars, out var cut);
            truncated |= cut;
            hits.Add(new SearchHit(ResolveSessionId(conn, sourceType, sourceId), sourceType, sourceId, text));
        }

        return new SearchResult(terms!, tokens, take, hits) { Truncated = truncated };
    }

    public SessionsResult Sessions(SessionQuery q)
    {
        var take = Caps.ClampLimit(q.Limit, out var clamped);
        var from = NormalizeBound(q.From, "--from", endOfDay: false);
        var to = NormalizeBound(q.To, "--to", endOfDay: true);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH t AS (
                SELECT session_id, MAX(timestamp) AS last_ts, COUNT(*) AS n FROM turns GROUP BY session_id
            )
            SELECT s.id, s.summary, s.repository, s.cwd, s.branch, s.created_at, t.last_ts, COALESCE(t.n, 0)
            FROM sessions s LEFT JOIN t ON t.session_id = s.id
            WHERE ($repo IS NULL OR s.repository LIKE $repo ESCAPE '\')
              AND ($cwd IS NULL OR s.cwd LIKE $cwd ESCAPE '\')
              AND ($branch IS NULL OR s.branch LIKE $branch ESCAPE '\')
              AND ($from IS NULL OR COALESCE(t.last_ts, s.created_at) >= $from)
              AND ($to IS NULL OR COALESCE(t.last_ts, s.created_at) < $to)
            ORDER BY COALESCE(t.last_ts, s.created_at) DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$repo", Contains(q.Repo));
        cmd.Parameters.AddWithValue("$cwd", Contains(q.Cwd));
        cmd.Parameters.AddWithValue("$branch", Contains(q.Branch));
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", take + 1);

        var list = new List<SessionSummary>();
        var truncated = clamped;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (list.Count == take) { truncated = true; break; }
                var id = r.GetString(0);
                var (title, source, cut) = ResolveTitle(id, Str(r, 1));
                truncated |= cut;
                list.Add(new SessionSummary(id, title, source, Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6), r.GetInt32(7)));
            }
        }

        return new SessionsResult(take, list) { Truncated = truncated };
    }

    public ShowResult Show(string id)
    {
        StorePaths.ValidateSessionId(id);
        using var conn = Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.summary, s.repository, s.cwd, s.branch, s.host_type, s.created_at,
                   (SELECT MAX(timestamp) FROM turns WHERE session_id = s.id),
                   (SELECT COUNT(*) FROM turns WHERE session_id = s.id)
            FROM sessions s WHERE s.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);

        SessionSummary summary;
        string? hostType, rawSummary;
        var truncated = false;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw NotFound(id);
            rawSummary = Str(r, 1);
            var (title, source, cut) = ResolveTitle(id, rawSummary);
            truncated |= cut;
            hostType = Str(r, 5);
            summary = new SessionSummary(id, title, source, Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 6), Str(r, 7), r.GetInt32(8));
        }

        var ws = ReadWorkspace(id);
        string? cappedSummary = null;
        if (rawSummary is not null)
        {
            cappedSummary = TextUtil.Cap(TextUtil.Collapse(rawSummary), Caps.SnippetChars, out var sumCut);
            truncated |= sumCut;
        }

        var solution = SolutionFinder.Find(_paths.EventsPath(id));

        var checkpoints = new List<CheckpointTitle>();
        using (var cp = conn.CreateCommand())
        {
            cp.CommandText = "SELECT checkpoint_number, title FROM checkpoints WHERE session_id = $id ORDER BY checkpoint_number LIMIT $n";
            cp.Parameters.AddWithValue("$id", id);
            cp.Parameters.AddWithValue("$n", Caps.MaxCheckpoints + 1);
            using var r = cp.ExecuteReader();
            while (r.Read())
            {
                if (checkpoints.Count == Caps.MaxCheckpoints) { truncated = true; break; }
                var t = Str(r, 1);
                if (t is not null) { t = TextUtil.Cap(TextUtil.Collapse(t), Caps.TitleChars, out var cut); truncated |= cut; }
                checkpoints.Add(new CheckpointTitle(r.GetInt64(0), t));
            }
        }

        return new ShowResult(summary, hostType, cappedSummary, ws.UserNamed,
            solution ?? "unknown", solution is null ? "none" : "events.jsonl", ws.Exists, checkpoints)
        { Truncated = truncated };
    }

    public TurnsResult Turns(string id, int? index, (int From, int To)? range, int? maxChars)
    {
        StorePaths.ValidateSessionId(id);
        if (index is not null && range is not null)
            throw new ToolkitException(ErrorKind.Usage, "Use either --index or --range, not both.");

        var perField = Math.Clamp(maxChars ?? Caps.DefaultTurnChars, 1, Caps.MaxTurnChars);
        var truncated = maxChars > Caps.MaxTurnChars;

        int a, b;
        if (index is not null) { a = b = index.Value; }
        else if (range is not null) { (a, b) = range.Value; }
        else { a = 0; b = Caps.MaxTurnsPerCall - 1; }
        if (a < 0 || b < a) throw new ToolkitException(ErrorKind.Usage, "Turn indexes must be zero or greater and the range must run upwards.");
        if ((long)b - a + 1 > Caps.MaxTurnsPerCall) { b = (int)Math.Min((long)a + Caps.MaxTurnsPerCall - 1, int.MaxValue); truncated = true; }

        using var conn = Open();
        EnsureSessionExists(conn, id);

        var total = ScalarInt(conn, "SELECT COUNT(*) FROM turns WHERE session_id = $id", id);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT turn_index, timestamp, user_message, assistant_response FROM turns
            WHERE session_id = $id AND turn_index BETWEEN $a AND $b ORDER BY turn_index
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$a", a);
        cmd.Parameters.AddWithValue("$b", b);

        var budget = new CharBudget(Caps.TurnsBudgetChars);
        var items = new List<TurnItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var user = Str(r, 2);
                var assistant = Str(r, 3);
                var cut = false;
                var u = budget.Take(user, perField, ref cut);
                var asst = budget.Take(assistant, perField, ref cut);
                truncated |= cut;
                items.Add(new TurnItem(r.GetInt64(0), Str(r, 1), u, asst, user?.Length ?? 0, assistant?.Length ?? 0, cut));
            }
        }

        return new TurnsResult(id, total, items) { Truncated = truncated };
    }

    public CheckpointsResult Checkpoints(string id, bool full)
    {
        StorePaths.ValidateSessionId(id);
        using var conn = Open();
        EnsureSessionExists(conn, id);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT checkpoint_number, title, overview, history, work_done, technical_details, important_files, next_steps, created_at
            FROM checkpoints WHERE session_id = $id ORDER BY checkpoint_number LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", Caps.MaxCheckpoints + 1);

        var budget = new CharBudget(Caps.CheckpointsBudgetChars);
        var truncated = false;
        var items = new List<CheckpointItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (items.Count == Caps.MaxCheckpoints) { truncated = true; break; }
                var title = budget.Take(Str(r, 1), Caps.TitleChars, ref truncated);
                var overview = budget.Take(Str(r, 2), full ? Caps.CheckpointFieldChars : Caps.CheckpointOverviewChars, ref truncated);
                items.Add(full
                    ? new CheckpointItem(r.GetInt64(0), title, overview, Str(r, 8),
                        budget.Take(Str(r, 3), Caps.CheckpointFieldChars, ref truncated),
                        budget.Take(Str(r, 4), Caps.CheckpointFieldChars, ref truncated),
                        budget.Take(Str(r, 5), Caps.CheckpointFieldChars, ref truncated),
                        budget.Take(Str(r, 6), Caps.CheckpointFieldChars, ref truncated),
                        budget.Take(Str(r, 7), Caps.CheckpointFieldChars, ref truncated))
                    : new CheckpointItem(r.GetInt64(0), title, overview, Str(r, 8), null, null, null, null, null));
            }
        }

        return new CheckpointsResult(id, full, items) { Truncated = truncated };
    }

    public StatsResult Stats()
    {
        using var conn = Open();
        var truncated = false;

        var sessions = ScalarInt(conn, "SELECT COUNT(*) FROM sessions", null);
        var turns = ScalarInt(conn, "SELECT COUNT(*) FROM turns", null);
        var byRepo = Counts(conn, "COALESCE(NULLIF(repository, ''), 'unknown')", "ORDER BY 2 DESC, 1", ref truncated);
        var byCwd = Counts(conn, "COALESCE(NULLIF(cwd, ''), 'unknown')", "ORDER BY 2 DESC, 1", ref truncated);
        var byMonth = Counts(conn, "COALESCE(NULLIF(substr(created_at, 1, 7), ''), 'unknown')", "ORDER BY 1 DESC", ref truncated);

        return new StatsResult(sessions, turns, byRepo, byCwd, byMonth) { Truncated = truncated };
    }

    // ---- helpers ----

    private SqliteConnection Open() => StoreConnection.OpenReadOnly(_paths);

    private static ToolkitException NotFound(string id) =>
        new(ErrorKind.NotFound, $"No session with id '{id}' in session-store.db.");

    private static void EnsureSessionExists(SqliteConnection conn, string id)
    {
        if (ScalarInt(conn, "SELECT COUNT(*) FROM sessions WHERE id = $id", id) == 0) throw NotFound(id);
    }

    private static int ScalarInt(SqliteConnection conn, string sql, string? id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (id is not null) cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<CountItem> Counts(SqliteConnection conn, string keyExpr, string order, ref bool truncated)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {keyExpr} AS k, COUNT(*) FROM sessions GROUP BY k {order} LIMIT {Caps.StatsTop + 1}";
        var list = new List<CountItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (list.Count == Caps.StatsTop) { truncated = true; break; }
            var key = TextUtil.Cap(TextUtil.Collapse(r.GetString(0)), Caps.TitleChars, out var cut);
            truncated |= cut;
            list.Add(new CountItem(key, r.GetInt32(1)));
        }
        return list;
    }

    private static List<(string Snippet, string SourceType, string SourceId)> QuerySnippets(SqliteConnection conn, string match, int take)
    {
        try
        {
            return RunSearch(conn, "snippet(search_index, 0, '[', ']', '...', 32)", match, take);
        }
        catch (SqliteException)
        {
            // snippet() is unavailable on some FTS5 table kinds; fall back to the start of the content.
            return RunSearch(conn, $"substr(content, 1, {Caps.SnippetChars})", match, take);
        }
    }

    private static List<(string, string, string)> RunSearch(SqliteConnection conn, string snippetExpr, string match, int take)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {snippetExpr}, source_type, source_id FROM search_index WHERE search_index MATCH $q ORDER BY rank LIMIT $n";
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$n", take);
        var rows = new List<(string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((Str(r, 0) ?? "", Str(r, 1) ?? "", Str(r, 2) ?? ""));
        return rows;
    }

    /// <summary>
    /// search_index carries no session column, so the owner is resolved from source_id: a session id, a
    /// "&lt;session id&gt;:..." prefix, or a turns.id. Unresolvable rows report null (the raw source_id is still returned).
    /// </summary>
    private static string? ResolveSessionId(SqliteConnection conn, string sourceType, string sourceId)
    {
        if (sourceId.Length == 0) return null;
        if (Exists(conn, "SELECT 1 FROM sessions WHERE id = $x", sourceId)) return sourceId;

        var colon = sourceId.IndexOf(':');
        if (colon > 0)
        {
            var prefix = sourceId[..colon];
            if (Exists(conn, "SELECT 1 FROM sessions WHERE id = $x", prefix)) return prefix;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id FROM turns WHERE id = $x";
        cmd.Parameters.AddWithValue("$x", sourceId);
        return cmd.ExecuteScalar() as string;
    }

    private static bool Exists(SqliteConnection conn, string sql, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$x", value);
        return cmd.ExecuteScalar() is not null;
    }

    private (string? Title, string Source, bool Cut) ResolveTitle(string id, string? summary)
    {
        var ws = ReadWorkspace(id);
        if (!string.IsNullOrWhiteSpace(ws.Name))
        {
            var t = TextUtil.Cap(TextUtil.Collapse(ws.Name), Caps.TitleChars, out var cut);
            return (t, "name", cut);
        }
        if (!string.IsNullOrWhiteSpace(summary))
        {
            var t = TextUtil.Cap(TextUtil.Collapse(summary), Caps.TitleChars, out var cut);
            return (t, "summary", cut);
        }
        return (null, "none", false);
    }

    private sealed record WorkspaceInfo(bool Exists, string? Name, bool? UserNamed);

    private WorkspaceInfo ReadWorkspace(string id)
    {
        try
        {
            var path = _paths.WorkspaceYamlPath(id);
            if (!File.Exists(path)) return new WorkspaceInfo(false, null, null);

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var yaml = WorkspaceYaml.Parse(ms.ToArray(), strict: false);
            var userNamed = yaml.GetValue("user_named");
            return new WorkspaceInfo(true, yaml.GetValue("name"),
                userNamed is null ? null : userNamed.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return new WorkspaceInfo(false, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new WorkspaceInfo(false, null, null);
        }
    }

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);

    private static object Contains(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? DBNull.Value
            : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    /// <summary>Accepts yyyy-MM-dd or a full ISO timestamp; a date-only "to" is inclusive (next day, exclusive bound).</summary>
    private static string? NormalizeBound(string? value, string option, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (!DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            throw new ToolkitException(ErrorKind.Usage, $"{option} must be a date (yyyy-MM-dd) or an ISO-8601 timestamp.");
        if (v.Length == 10 && endOfDay) dt = dt.AddDays(1);
        return dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
