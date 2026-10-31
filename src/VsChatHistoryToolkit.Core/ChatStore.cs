using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VsChatHistoryToolkit;

/// <summary>Read-only queries over session-store.db. Every method opens its own read-only connection and returns capped results.</summary>
public sealed class ChatStore
{
    private readonly StorePaths _paths;

    public ChatStore(StorePaths paths) => _paths = paths;

    /// <summary>Test seam for doctor: lists the folders directly under a folder.</summary>
    internal Func<string, IEnumerable<string>> ListDirectories { get; init; } = Directory.EnumerateDirectories;

    /// <summary>Test seam for doctor: the size of a file, or null when it does not exist.</summary>
    internal Func<string, long?> FileLength { get; init; } = path => new FileInfo(path) is { Exists: true } f ? f.Length : null;

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
        foreach (var (snippet, sessionId, sourceType, sourceId) in rows.Take(take))
        {
            var text = TextUtil.Cap(TextUtil.Collapse(snippet), Caps.SnippetChars, out var cut);
            truncated |= cut;
            hits.Add(new SearchHit(string.IsNullOrEmpty(sessionId) ? null : sessionId, sourceType, sourceId, text));
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

        var scan = SolutionFinder.Scan(_paths.EventsPath(id));
        var solution = scan.Solution;
        // "unknown" is only a safe answer when the whole file was scanned.
        if (solution is null && scan.Incomplete) truncated = true;
        if (scan.SolutionCut) truncated = true;

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
            solution ?? "unknown", solution is not null ? "events.jsonl" : scan.Incomplete ? "incomplete" : "none", ws.Exists, checkpoints)
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

        UsageOverview? usage = null;
        if (UsageTableReady(conn))
        {
            try { usage = UsageOverviewOf(conn, ref truncated); }
            catch (SqliteException ex) when (IsOverflow(ex)) { truncated = true; }   // a stored figure is absurdly large: leave usage out rather than fail stats
        }

        return new StatsResult(sessions, turns, byRepo, byCwd, byMonth, usage) { Truncated = truncated };
    }

    /// <summary>
    /// Chats ranked by usage from assistant_usage_events, filtered on each request's created_at. A store without a usable
    /// usage table is a normal answer (Recorded = false); a stored figure too large to add up is store_unavailable.
    /// </summary>
    public UsageTopResult UsageTop(string? from, string? to, string? by, int? limit)
    {
        var key = (by ?? "aiu").Trim().ToLowerInvariant();
        var metric = key switch
        {
            "aiu" => "SUM(COALESCE(total_nano_aiu, 0))",
            "tokens" => "SUM(input_tokens) + SUM(output_tokens)",
            "requests" => "COUNT(*)",
            _ => throw new ToolkitException(ErrorKind.Usage, "--by must be one of: requests, tokens, aiu."),
        };
        var take = Caps.ClampLimit(limit, out var clamped);
        var lo = NormalizeBound(from, "--from", endOfDay: false);
        var hi = NormalizeBound(to, "--to", endOfDay: true);

        try { return UsageTopCore(key, metric, lo, hi, take, clamped); }
        catch (SqliteException ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A usage figure is too large to add up (SQLite integer overflow); the stored values look wrong.");
        }
    }

    private UsageTopResult UsageTopCore(string by, string metric, string? from, string? to, int take, bool clamped)
    {
        using var conn = Open();
        if (!UsageTableReady(conn))
            return new UsageTopResult(by, from, to, false,
                "This store has no usage table (or it has an unexpected shape), so no token figures are available.", take, Array.Empty<UsageTopItem>());

        // metric is one of three constant expressions chosen above, never user text.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH u AS (
                SELECT session_id, COUNT(*) AS n, COALESCE(SUM(input_tokens), 0) AS i, COALESCE(SUM(output_tokens), 0) AS o,
                       COALESCE(SUM(cache_read_tokens), 0) AS cr, COALESCE(SUM(reasoning_tokens), 0) AS r,
                       COALESCE(SUM(total_nano_aiu), 0) AS aiu, MAX(created_at) AS last_at, {metric} AS score
                FROM assistant_usage_events
                WHERE ($from IS NULL OR created_at >= $from) AND ($to IS NULL OR created_at < $to)
                GROUP BY session_id
            )
            SELECT u.session_id, s.summary, u.n, u.i, u.o, u.cr, u.r, u.aiu, u.last_at
            FROM u LEFT JOIN sessions s ON s.id = u.session_id
            ORDER BY u.score DESC, u.session_id
            LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", take + 1);

        var truncated = clamped;
        var list = new List<UsageTopItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (list.Count == take) { truncated = true; break; }
                var sid = Str(r, 0) ?? "";
                var (title, source, cut) = IsSessionId(sid) ? ResolveTitle(sid, Str(r, 1)) : (null, "none", false);
                truncated |= cut;
                list.Add(new UsageTopItem(sid, title, source, r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), Str(r, 8)));
            }
        }

        return new UsageTopResult(by, from, to, true, null, take, list) { Truncated = truncated };
    }

    /// <summary>
    /// Read-only health report comparing session rows with session-state folders. It only lists folders and reads file
    /// sizes; it never opens, changes or deletes anything on disk, and the database is opened read-only.
    /// </summary>
    public DoctorResult Doctor()
    {
        using var conn = Open();
        var truncated = false;

        var rows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM sessions";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (Str(r, 0) is { } id) rows.Add(id);
        }
        var sessionRows = ScalarInt(conn, "SELECT COUNT(*) FROM sessions", null);
        var withoutTurns = ScalarInt(conn,
            "SELECT COUNT(*) FROM sessions s WHERE NOT EXISTS (SELECT 1 FROM turns t WHERE t.session_id = s.id)", null);
        int? withoutUsage = UsageTableReady(conn)
            ? ScalarInt(conn, "SELECT COUNT(*) FROM sessions s WHERE NOT EXISTS (SELECT 1 FROM assistant_usage_events u WHERE u.session_id = s.id)", null)
            : null;

        var dir = _paths.SessionStateDir;
        var present = Directory.Exists(dir);
        var folders = new List<string>();
        // A missing session-state folder is a known state (no folders); only a folder that exists but cannot be listed
        // leaves the folder figures unknown. A partial listing is discarded rather than reported as complete.
        var listed = true;
        if (present)
        {
            try { folders.AddRange(ListDirectories(dir).Select(Path.GetFileName).OfType<string>()); }
            catch (IOException) { listed = false; }
            catch (UnauthorizedAccessException) { listed = false; }
            if (!listed) { folders.Clear(); truncated = true; }
        }
        var folderSet = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);

        var rowsWithoutFolder = listed ? rows.Where(id => !folderSet.Contains(id)).Order(StringComparer.Ordinal).ToList() : new List<string>();
        var foldersWithoutRow = folders.Where(f => !rows.Contains(f)).Order(StringComparer.Ordinal).ToList();

        var sizes = new List<EventsFileItem>();
        foreach (var f in folders)
        {
            try
            {
                if (FileLength(Path.Combine(dir, f, "events.jsonl")) is { } bytes) sizes.Add(new EventsFileItem(f, bytes));
            }
            catch (IOException) { truncated = true; }
            catch (UnauthorizedAccessException) { truncated = true; }
        }
        if (sizes.Count > Caps.DoctorLargestFiles) truncated = true;
        var largest = new List<EventsFileItem>();
        foreach (var e in sizes.OrderByDescending(s => s.Bytes).ThenBy(s => s.SessionId, StringComparer.Ordinal).Take(Caps.DoctorLargestFiles))
            largest.Add(e with { SessionId = CapName(e.SessionId, ref truncated) });

        var rowsSample = Sample(rowsWithoutFolder, Caps.StatsTop, ref truncated);
        var foldersSample = Sample(foldersWithoutRow, Caps.StatsTop, ref truncated);

        return new DoctorResult(sessionRows, present, listed,
            listed ? folders.Count : null, listed ? rowsWithoutFolder.Count : null, rowsSample,
            listed ? foldersWithoutRow.Count : null, foldersSample, withoutTurns, withoutUsage, largest)
        { Truncated = truncated };
    }

    private static List<string> Sample(List<string> all, int max, ref bool truncated)
    {
        if (all.Count > max) truncated = true;
        var list = new List<string>();
        foreach (var s in all.Take(max)) list.Add(CapName(s, ref truncated));
        return list;
    }

    /// <summary>Folder names and stored ids are not guaranteed to have the session id shape, so they are capped like titles.</summary>
    private static string CapName(string s, ref bool truncated)
    {
        var c = TextUtil.Cap(s, Caps.TitleChars, out var cut);
        truncated |= cut;
        return c;
    }

    /// <summary>
    /// Token and cost figures of one session, summed from the per-request rows in assistant_usage_events. A store
    /// without that table, or a session without rows, is a normal answer (Recorded = false), not an error.
    /// </summary>
    public UsageResult Usage(string id, bool byTurn)
    {
        try { return UsageCore(id, byTurn); }
        catch (SqliteException ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A usage figure in this session is too large to add up (SQLite integer overflow); the stored values look wrong.");
        }
    }

    private static bool IsOverflow(SqliteException ex) => ex.Message.Contains("integer overflow", StringComparison.OrdinalIgnoreCase);

    private UsageResult UsageCore(string id, bool byTurn)
    {
        StorePaths.ValidateSessionId(id);
        using var conn = Open();
        EnsureSessionExists(conn, id);

        if (!UsageTableReady(conn))
            return NoUsage(id, "This store has no usage table (or it has an unexpected shape), so no token figures are available.");

        var truncated = false;
        UsageTotals? main = null, sub = null;
        string? first = null, last = null;
        long? peakInput = null, lastInput = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT (agent_id IS NOT NULL) AS sub, {UsageAggregates}, MIN(created_at), MAX(created_at), MAX(input_tokens)
                FROM assistant_usage_events WHERE session_id = $id GROUP BY sub
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var totals = ReadTotals(r, 1);
                if (r.GetInt64(0) == 0) { main = totals; peakInput = r.IsDBNull(11) ? null : r.GetInt64(11); }
                else sub = totals;
                first = Earlier(first, Str(r, 9));
                last = Later(last, Str(r, 10));
            }
        }

        if (main is null && sub is null)
            return NoUsage(id, "No usage rows are recorded for this session.");

        if (main is not null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT input_tokens FROM assistant_usage_events WHERE session_id = $id AND agent_id IS NULL
                ORDER BY created_at DESC, rowid DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", id);
            var v = cmd.ExecuteScalar();
            lastInput = v is null || v is DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        var total = Add(main, sub, ref truncated);

        var byModel = new List<UsageModelItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(NULLIF(model, ''), 'unknown') AS m, {UsageAggregates}
                FROM assistant_usage_events WHERE session_id = $id GROUP BY m ORDER BY COUNT(*) DESC, m LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", Caps.StatsTop + 1);
            ReadModels(cmd, byModel, ref truncated);
        }

        List<UsageTurnItem>? turns = null;
        if (byTurn)
        {
            turns = new List<UsageTurnItem>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT turn_index, {UsageAggregates}, MAX(CASE WHEN agent_id IS NULL THEN input_tokens END)
                FROM assistant_usage_events WHERE session_id = $id GROUP BY turn_index ORDER BY turn_index LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", Caps.MaxUsageTurns + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (turns.Count == Caps.MaxUsageTurns) { truncated = true; break; }
                turns.Add(new UsageTurnItem(r.GetInt64(0), ReadTotals(r, 1), r.IsDBNull(9) ? null : r.GetInt64(9)));
            }
        }

        return new UsageResult(id, true, null, total, main ?? Empty, sub ?? Empty, first, last, peakInput, lastInput, byModel, turns)
            { Truncated = truncated };
    }

    /// <summary>
    /// Files edited or created in one chat, from session_files, ordered by turn then row id. A store without that table
    /// (or with an unexpected shape), or a chat without rows, is a normal answer (Recorded = false), not an error.
    /// </summary>
    public FilesResult Files(string id, int? limit)
    {
        StorePaths.ValidateSessionId(id);
        var take = Caps.ClampLimit(limit, out var clamped);
        using var conn = Open();
        EnsureSessionExists(conn, id);

        if (!FilesTableReady(conn))
            return new FilesResult(id, false, NoFilesTable, take, Array.Empty<FileItem>());

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT file_path, tool_name, turn_index, first_seen_at FROM session_files
            WHERE session_id = $id ORDER BY turn_index, id LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", take + 1);

        var truncated = clamped;
        var items = new List<FileItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (items.Count == take) { truncated = true; break; }
                var path = TextUtil.Cap(Str(r, 0) ?? "", Caps.FilePathChars, out var pathCut);   // as stored: a listed path must work as --path
                var tool = Str(r, 1);
                var toolCut = false;
                if (tool is not null) tool = TextUtil.Cap(TextUtil.Collapse(tool), Caps.TitleChars, out toolCut);
                truncated |= pathCut | toolCut;
                items.Add(new FileItem(path, tool, r.IsDBNull(2) ? null : r.GetInt64(2), Str(r, 3)));
            }
        }

        if (items.Count == 0)
            return new FilesResult(id, false, "No edited or created files are recorded for this session.", take, items) { Truncated = truncated };

        return new FilesResult(id, true, null, take, items) { Truncated = truncated };
    }

    /// <summary>
    /// Chats that touched a file whose path contains <paramref name="path"/> (case-insensitive), one row per chat,
    /// newest touch first. A store without a usable session_files table is a normal answer (Recorded = false).
    /// </summary>
    public FilesByPathResult FilesByPath(string path, int? limit)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolkitException(ErrorKind.Usage, "--path needs some text to look for.");
        var take = Caps.ClampLimit(limit, out var clamped);
        var shownPath = TextUtil.Cap(path.Trim(), Caps.FilePathChars, out var queryCut);

        using var conn = Open();
        if (!FilesTableReady(conn))
            return new FilesByPathResult(shownPath, false, NoFilesTable, take, Array.Empty<FileSessionItem>()) { Truncated = queryCut };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.session_id, s.summary, COUNT(DISTINCT f.file_path), MAX(f.turn_index), MAX(f.first_seen_at) AS last_seen
            FROM session_files f LEFT JOIN sessions s ON s.id = f.session_id
            WHERE f.file_path LIKE $p ESCAPE '\'
            GROUP BY f.session_id
            ORDER BY last_seen DESC, f.session_id
            LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$p", Contains(path));
        cmd.Parameters.AddWithValue("$n", take + 1);

        var truncated = clamped || queryCut;
        var list = new List<FileSessionItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (list.Count == take) { truncated = true; break; }
                var sid = Str(r, 0) ?? "";
                // Ids come from the store; only well-formed ones are used to look for a workspace.yaml title.
                var (title, source, cut) = IsSessionId(sid) ? ResolveTitle(sid, Str(r, 1)) : (null, "none", false);
                truncated |= cut;
                list.Add(new FileSessionItem(sid, title, source, r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3), Str(r, 4)));
            }
        }

        return new FilesByPathResult(shownPath, true, null, take, list) { Truncated = truncated };
    }

    private const string NoFilesTable =
        "This store has no session_files table (or it has an unexpected shape), so no edited files are available.";

    private static readonly string[] FilesColumns = { "id", "session_id", "file_path", "tool_name", "turn_index", "first_seen_at" };

    private static bool FilesTableReady(SqliteConnection conn) => HasColumns(conn, "session_files", FilesColumns);

    private static bool IsSessionId(string id)
    {
        try { StorePaths.ValidateSessionId(id); return true; }
        catch (ToolkitException) { return false; }
    }

    // ---- usage helpers ----

    private static readonly UsageTotals Empty = new(0, 0, 0, 0, 0, 0, 0, 0);

    private const string UsageAggregates =
        "COUNT(*), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), COALESCE(SUM(cache_read_tokens), 0), " +
        "COALESCE(SUM(cache_write_tokens), 0), COALESCE(SUM(reasoning_tokens), 0), COALESCE(SUM(total_nano_aiu), 0), COALESCE(SUM(duration_ms), 0)";

    private static readonly string[] UsageColumns =
    {
        "session_id", "turn_index", "agent_id", "model", "input_tokens", "output_tokens", "cache_read_tokens",
        "cache_write_tokens", "reasoning_tokens", "total_nano_aiu", "duration_ms", "created_at",
    };

    private static UsageResult NoUsage(string id, string note) =>
        new(id, false, note, null, null, null, null, null, null, null, Array.Empty<UsageModelItem>(), null);

    /// <summary>True when assistant_usage_events exists with every column this tool reads.</summary>
    private static bool UsageTableReady(SqliteConnection conn) => HasColumns(conn, "assistant_usage_events", UsageColumns);

    /// <summary>True when <paramref name="table"/> exists with every listed column. The table name is a constant, never user input.</summary>
    private static bool HasColumns(SqliteConnection conn, string table, string[] columns)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) have.Add(r.GetString(0));
        return columns.All(have.Contains);
    }

    /// <summary>Reads the eight aggregate columns that start at <paramref name="o"/>.</summary>
    private static UsageTotals ReadTotals(SqliteDataReader r, int o) =>
        new(r.GetInt64(o), r.GetInt64(o + 1), r.GetInt64(o + 2), r.GetInt64(o + 3), r.GetInt64(o + 4), r.GetInt64(o + 5), r.GetInt64(o + 6), r.GetInt64(o + 7));

    /// <summary>Adds two totals; a sum that would not fit in a long stops at long.MaxValue and sets <paramref name="saturated"/>.</summary>
    private static UsageTotals Add(UsageTotals? a, UsageTotals? b, ref bool saturated)
    {
        a ??= Empty; b ??= Empty;
        var s = saturated;
        long Sum(long x, long y)
        {
            try { return checked(x + y); }
            catch (OverflowException) { s = true; return long.MaxValue; }
        }
        var t = new UsageTotals(Sum(a.Requests, b.Requests), Sum(a.InputTokens, b.InputTokens), Sum(a.OutputTokens, b.OutputTokens),
            Sum(a.CacheReadTokens, b.CacheReadTokens), Sum(a.CacheWriteTokens, b.CacheWriteTokens), Sum(a.ReasoningTokens, b.ReasoningTokens),
            Sum(a.NanoAiu, b.NanoAiu), Sum(a.DurationMs, b.DurationMs));
        saturated = s;
        return t;
    }

    private static string? Earlier(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;

    private static string? Later(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;

    /// <summary>Command text must select the model name first, then the eight aggregates, and use LIMIT StatsTop + 1.</summary>
    private static void ReadModels(SqliteCommand cmd, List<UsageModelItem> into, ref bool truncated)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (into.Count == Caps.StatsTop) { truncated = true; break; }
            var name = TextUtil.Cap(TextUtil.Collapse(r.GetString(0)), Caps.TitleChars, out var cut);
            truncated |= cut;
            into.Add(new UsageModelItem(name, ReadTotals(r, 1)));
        }
    }

    private static UsageOverview UsageOverviewOf(SqliteConnection conn, ref bool truncated)
    {
        UsageTotals total;
        int sessions;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(DISTINCT session_id), {UsageAggregates} FROM assistant_usage_events";
            using var r = cmd.ExecuteReader();
            r.Read();
            sessions = r.GetInt32(0);
            total = ReadTotals(r, 1);
        }

        var byModel = new List<UsageModelItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(NULLIF(model, ''), 'unknown') AS m, {UsageAggregates}
                FROM assistant_usage_events GROUP BY m ORDER BY COUNT(*) DESC, m LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", Caps.StatsTop + 1);
            ReadModels(cmd, byModel, ref truncated);
        }

        var byMonth = new List<UsageMonthItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(NULLIF(substr(created_at, 1, 7), ''), 'unknown') AS k, {UsageAggregates}
                FROM assistant_usage_events GROUP BY k ORDER BY 1 DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", Caps.StatsTop + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (byMonth.Count == Caps.StatsTop) { truncated = true; break; }
                byMonth.Add(new UsageMonthItem(TextUtil.Cap(TextUtil.Collapse(r.GetString(0)), Caps.TitleChars, out _), ReadTotals(r, 1)));
            }
        }

        return new UsageOverview(sessions, total, byModel, byMonth);
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

    private static List<(string Snippet, string? SessionId, string SourceType, string SourceId)> QuerySnippets(SqliteConnection conn, string match, int take)
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

    /// <summary>search_index carries its own session_id column (UNINDEXED), so hits need no lookup to find their session.</summary>
    private static List<(string Snippet, string? SessionId, string SourceType, string SourceId)> RunSearch(SqliteConnection conn, string snippetExpr, string match, int take)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {snippetExpr}, session_id, source_type, source_id FROM search_index WHERE search_index MATCH $q ORDER BY rank LIMIT $n";
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$n", take);
        var rows = new List<(string, string?, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((Str(r, 0) ?? "", Str(r, 1), Str(r, 2) ?? "", Str(r, 3) ?? ""));
        return rows;
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
