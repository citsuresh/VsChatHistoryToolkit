using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VsChatHistoryToolkit;

/// <summary>Read-only queries over session-store.db. Every method opens its own read-only connection and returns capped results.</summary>
public sealed class ChatStore
{
    private readonly StorePaths _paths;
    private readonly Func<DateTimeOffset>? _clock;

    /// <param name="clock">Optional time source for the rollup time budget; null measures real elapsed time with a stopwatch.</param>
    public ChatStore(StorePaths paths, Func<DateTimeOffset>? clock = null)
    {
        _paths = paths;
        _clock = clock;
    }

    /// <summary>Test seam for rollup: the total byte budget across all scans.</summary>
    internal long RollupMaxBytes { get; init; } = Caps.RollupMaxBytes;

    /// <summary>Test seam for rollup: scans one events.jsonl (session id, path).</summary>
    internal Func<string, string, EventsResult> ScanEvents { get; init; } = EventsSummaryScanner.Scan;

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
            catch (Exception ex) when (IsOverflow(ex)) { truncated = true; }   // a stored figure is absurdly large: leave usage out rather than fail stats
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
        if (key is not ("aiu" or "tokens" or "requests" or "credits"))
            throw new ToolkitException(ErrorKind.Usage, "--by must be one of: requests, tokens, aiu, credits.");
        var take = Caps.ClampLimit(limit, out var clamped);
        var lo = NormalizeBound(from, "--from", endOfDay: false);
        var hi = NormalizeBound(to, "--to", endOfDay: true);

        try { return UsageTopCore(key, lo, hi, take, clamped); }
        catch (Exception ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A usage figure is too large to add up (SQLite integer or real overflow); the stored values look wrong.");
        }
    }

    private UsageTopResult UsageTopCore(string by, string? from, string? to, int take, bool clamped)
    {
        using var conn = Open();
        if (!UsageTableReady(conn))
            return new UsageTopResult(by, from, to, false,
                "This store has no usage table (or it has an unexpected shape), so no token figures are available.", take, Array.Empty<UsageTopItem>());

        var credits = CreditsColumnPresent(conn);
        string? note = null;
        // metric is one of these constant expressions, never user text.
        var metric = by switch
        {
            "aiu" => "SUM(COALESCE(total_nano_aiu, 0))",
            "tokens" => "SUM(input_tokens) + SUM(output_tokens)",
            "requests" => "COUNT(*)",
            _ => credits ? $"ROUND({CreditsSum}, 2)" : "NULL",
        };
        if (by == "credits" && !credits)
            note = "This store's usage table has no request_multiplier column, so credits are not available; chats are listed by session id.";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH u AS (
                SELECT session_id, COUNT(*) AS n, COALESCE(SUM(input_tokens), 0) AS i, COALESCE(SUM(output_tokens), 0) AS o,
                       COALESCE(SUM(cache_read_tokens), 0) AS cr, COALESCE(SUM(reasoning_tokens), 0) AS r,
                       COALESCE(SUM(total_nano_aiu), 0) AS aiu, MAX(created_at) AS last_at, {metric} AS score,
                       {(credits ? CreditsSum : "NULL")} AS credits
                FROM assistant_usage_events
                WHERE ($from IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', created_at) >= $from)
                  AND ($to IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', created_at) < $to)
                GROUP BY session_id
            )
            SELECT u.session_id, s.summary, u.n, u.i, u.o, u.cr, u.r, u.aiu, u.last_at, u.credits
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
                var n = r.GetInt64(2);
                list.Add(new UsageTopItem(sid, title, source, n, r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                    credits ? ReadCredits(r, 9, n) : null, Str(r, 8)));
            }
        }

        return new UsageTopResult(by, from, to, true, note, take, list) { Truncated = truncated };
    }

    public UsageBreakdownResult UsageBreakdown(
        string? from, string? to, string? repo, string? model, string? groupBy, int? limit)
    {
        var requested = (groupBy ?? "day,repo,model").Split(',').Select(x => x.Trim().ToLowerInvariant()).ToArray();
        string[] keys = { "day", "repo", "model" };
        if (requested.Any(x => !keys.Contains(x)) || requested.Distinct().Count() != requested.Length)
            throw new ToolkitException(ErrorKind.Usage, "--group-by must be a non-empty combination of day, repo, model, without duplicates.");
        var groups = keys.Where(requested.Contains).ToArray();
        var take = limit ?? Caps.UsageBreakdownDefaultLimit;
        if (take < 1) throw new ToolkitException(ErrorKind.Usage, "--limit must be at least 1.");
        var clamped = take > Caps.UsageBreakdownMaxLimit;
        take = Math.Min(take, Caps.UsageBreakdownMaxLimit);
        var lo = NormalizeBound(from, "--from", endOfDay: false);
        var hi = NormalizeBound(to, "--to", endOfDay: true);

        try { return UsageBreakdownCore(lo, hi, repo, model, groups, take, clamped); }
        catch (Exception ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A usage figure is too large to add up (SQLite integer or real overflow); the stored values look wrong.");
        }
    }

    private UsageBreakdownResult UsageBreakdownCore(
        string? from, string? to, string? repo, string? model, string[] groups, int take, bool clamped)
    {
        using var conn = Open();
        if (!HasColumns(conn, "assistant_usage_events",
            new[] { "session_id", "created_at", "model", "input_tokens", "output_tokens", "total_nano_aiu" }))
            return new UsageBreakdownResult(from, to, groups, false,
                "This store has no usage table (or it has an unexpected shape), so no token figures are available.",
                take, null, Array.Empty<UsageBreakdownItem>());

        var credits = CreditsColumnPresent(conn);
        var modelFilter = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        conn.CreateFunction<string?, string?, bool>("model_contains",
            (value, filter) => filter is null || value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
        // Group expressions are constants selected by the validated key list, never SQL from the caller.
        var day = groups.Contains("day") ? "request_day" : "NULL";
        var repository = groups.Contains("repo") ? "repository" : "NULL";
        var requestModel = groups.Contains("model") ? "request_model" : "NULL";
        var grouping = string.Join(", ", groups.Select(x => x switch
        {
            "day" => "request_day",
            "repo" => "repository",
            _ => "request_model",
        }));
        var aggregates = $"""
            COUNT(*) AS requests, COALESCE(SUM(total_nano_aiu), 0) AS nano_aiu,
            {(credits ? CreditsSum : "NULL")} AS credits,
            COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(output_tokens), 0) AS output_tokens,
            COUNT(DISTINCT session_id) AS chats
            """;
        using var cmd = conn.CreateCommand();
        // One statement keeps total and rows on the same read snapshot, including while VS records more requests.
        cmd.CommandText = $"""
            WITH filtered AS (
                SELECT u.session_id, strftime('%Y-%m-%d', u.created_at) AS request_day,
                       COALESCE(NULLIF(s.repository, ''), 'unknown') AS repository,
                       COALESCE(NULLIF(u.model, ''), 'unknown') AS request_model,
                       u.input_tokens, u.output_tokens, u.total_nano_aiu,
                       {(credits ? "u.request_multiplier" : "NULL")} AS request_multiplier
                FROM assistant_usage_events u LEFT JOIN sessions s ON s.id = u.session_id
                WHERE ($from IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', u.created_at) >= $from)
                  AND ($to IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', u.created_at) < $to)
                  AND ($repo IS NULL OR s.repository LIKE $repo ESCAPE '\')
                  AND model_contains(u.model, $model)
            ), grouped AS (
                SELECT {day} AS day, {repository} AS repo, {requestModel} AS model, {aggregates}
                FROM filtered GROUP BY {grouping}
            ), limited AS (
                SELECT 1 AS row_kind, * FROM grouped
                ORDER BY day DESC, nano_aiu DESC, repo, model LIMIT $n
            )
            SELECT 0 AS row_kind, NULL AS day, NULL AS repo, NULL AS model, {aggregates} FROM filtered
            UNION ALL SELECT * FROM limited
            ORDER BY row_kind, day DESC, nano_aiu DESC, repo, model
            """;
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$repo", Contains(repo));
        cmd.Parameters.AddWithValue("$model", (object?)modelFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", take + 1);

        using var r = cmd.ExecuteReader();
        r.Read();
        var total = ReadBreakdownTotals(r, credits);
        var rows = new List<UsageBreakdownItem>();
        var truncated = clamped;
        while (r.Read())
        {
            if (rows.Count == take) { truncated = true; break; }
            var totals = ReadBreakdownTotals(r, credits);
            rows.Add(new UsageBreakdownItem(Str(r, 1), Str(r, 2), Str(r, 3), totals.Requests, totals.NanoAiu,
                totals.Credits, totals.InputTokens, totals.OutputTokens, totals.Chats));
        }
        var note = truncated ? $"Rows are limited to {take}; total covers all filtered requests." : null;
        return new UsageBreakdownResult(from, to, groups, true, note, take, total, rows) { Truncated = truncated };
    }

    private static UsageBreakdownTotals ReadBreakdownTotals(SqliteDataReader r, bool credits) =>
        new(r.GetInt64(4), r.GetInt64(5), credits ? ReadCredits(r, 6, r.GetInt64(4)) : null,
            r.GetInt64(7), r.GetInt64(8), r.GetInt64(9));

    /// <summary>Returns a bounded summary of selected event metadata for one session.</summary>
    public EventsResult Events(string id)
    {
        StorePaths.ValidateSessionId(id);
        using var conn = Open();
        EnsureSessionExists(conn, id);
        return EventsSummaryScanner.Scan(id, _paths.EventsPath(id));
    }

    /// <summary>
    /// Combines the events summaries of the most recent chats (chosen like sessions: filtered on the session end,
    /// newest first). Scans run one after another under a total time and byte budget; once a budget would be
    /// exceeded no new scan starts and the remaining chats count as skipped. An unreadable log is skipped, never fatal.
    /// </summary>
    public RollupResult Rollup(string? from, string? to, int? limit)
    {
        var take = Caps.ClampLimit(limit, out var clamped);
        var fromBound = NormalizeBound(from, "--from", endOfDay: false);
        var toBound = NormalizeBound(to, "--to", endOfDay: true);
        var elapsed = StartTimer();

        var ids = new List<string>();
        var truncated = clamped;
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                WITH t AS (SELECT session_id, MAX(timestamp) AS last_ts FROM turns GROUP BY session_id)
                SELECT s.id FROM sessions s LEFT JOIN t ON t.session_id = s.id
                WHERE ($from IS NULL OR COALESCE(t.last_ts, s.created_at) >= $from)
                  AND ($to IS NULL OR COALESCE(t.last_ts, s.created_at) < $to)
                ORDER BY COALESCE(t.last_ts, s.created_at) DESC, s.id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$from", (object?)fromBound ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$to", (object?)toBound ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", take + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (ids.Count == take) { truncated = true; break; }
                ids.Add(r.GetString(0));
            }
        }

        var budget = TimeSpan.FromSeconds(Caps.RollupMaxSeconds);
        long scanned = 0, skipped = 0, withoutLog = 0, incomplete = 0, withoutShutdown = 0, bytesUsed = 0;
        var saturated = false;
        var exhausted = false;
        var results = new List<EventsResult>();
        foreach (var id in ids)
        {
            if (exhausted) { skipped++; continue; }

            long? length;
            string path;
            try
            {
                path = _paths.EventsPath(id);
                // A folder in place of the log cannot be read: skipped, as events reports it, not "without log".
                if (Directory.Exists(path))
                {
                    skipped++;
                    truncated = true;
                    continue;
                }
                length = FileLength(path);
            }
            catch (Exception ex) when (ex is ToolkitException or IOException or UnauthorizedAccessException)
            {
                skipped++;
                truncated = true;
                continue;
            }
            if (length is null) { withoutLog++; continue; }

            var projected = Math.Min(length.Value, Caps.EventsMaxBytes);
            if (elapsed() >= budget || projected > RollupMaxBytes - bytesUsed)
            {
                exhausted = true;
                truncated = true;
                skipped++;
                continue;
            }

            EventsResult result;
            try { result = ScanEvents(id, path); }
            catch (ToolkitException)
            {
                skipped++;
                truncated = true;
                continue;
            }
            if (!result.Present) { withoutLog++; continue; }

            bytesUsed += Math.Max(projected, result.Scan.BytesScanned);
            scanned++;
            results.Add(result);
            truncated |= result.Truncated;
            if (!result.Scan.Complete) incomplete++;
            else if (result.Shutdowns.Count == 0) withoutShutdown++;
        }

        var eventCounts = MergeTop(results.SelectMany(x => x.EventCounts.Select(e => (e.Type, e.Count))), ref truncated, ref saturated)
            .Select(x => new EventCountItem(x.Name, x.Count)).ToList();
        var tools = MergeTop(results.SelectMany(x => x.Tools.Select(e => (e.Name, e.Count))), ref truncated, ref saturated);
        var skills = MergeTop(results.SelectMany(x => x.Skills.Select(e => (e.Name, e.Count))), ref truncated, ref saturated);
        var kinds = MergeTop(results.SelectMany(x => x.PermissionKinds.Select(e => (e.Name, e.Count))), ref truncated, ref saturated);

        long aborts = 0, subagents = 0, compactionCount = 0;
        long premium = 0, nanoAiu = 0, added = 0, removed = 0;
        var pre = new List<long>();
        var tokensRemoved = new List<long>();
        var triggers = new List<(string, long)>();
        foreach (var x in results)
        {
            aborts = SaturatingAdd(aborts, x.Aborts, ref saturated);
            subagents = SaturatingAdd(subagents, x.SubagentsStarted, ref saturated);
            compactionCount = SaturatingAdd(compactionCount, x.CompactionCount, ref saturated);
            foreach (var c in x.Compactions)
            {
                triggers.Add((c.Trigger ?? "(none)", 1));
                if (c.PreCompactionTokens is { } p) pre.Add(p);
                if (c.TokensRemoved is { } t) tokensRemoved.Add(t);
            }
            foreach (var s in x.Shutdowns)
            {
                premium = SaturatingAdd(premium, s.TotalPremiumRequests ?? 0, ref saturated);
                nanoAiu = SaturatingAdd(nanoAiu, s.TotalNanoAiu ?? 0, ref saturated);
                added = SaturatingAdd(added, s.LinesAdded ?? 0, ref saturated);
                removed = SaturatingAdd(removed, s.LinesRemoved ?? 0, ref saturated);
            }
        }
        var byTrigger = MergeTop(triggers, ref truncated, ref saturated);

        var topChats = results.Where(x => x.CompactionCount > 0)
            .OrderByDescending(x => x.CompactionCount).ThenBy(x => x.SessionId, StringComparer.Ordinal)
            .Take(Caps.RollupTopChats)
            .Select(x => new RollupChatCount(x.SessionId, x.CompactionCount)).ToList();

        var compactions = new RollupCompactions(compactionCount, byTrigger, Median(pre), pre.Count == 0 ? null : pre.Max(), Median(tokensRemoved));
        return new RollupResult(fromBound, toBound, take, ids.Count, scanned, skipped, withoutLog, incomplete, withoutShutdown, bytesUsed,
            eventCounts, tools, skills, kinds, aborts, subagents, compactions,
            new RollupShutdownTotals(premium, nanoAiu, added, removed), topChats) { Truncated = truncated || saturated };
    }

    /// <summary>Returns a function giving the time since this call: from the injected clock, or a stopwatch.</summary>
    private Func<TimeSpan> StartTimer()
    {
        if (_clock is { } clock)
        {
            var start = clock();
            return () => clock() - start;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        return () => sw.Elapsed;
    }

    /// <summary>Sums counts per name, then keeps the top RollupTop by count (descending) and name (ordinal).</summary>
    private static List<EventNameCount> MergeTop(IEnumerable<(string Name, long Count)> items, ref bool truncated, ref bool saturated)
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, count) in items)
            sums[name] = SaturatingAdd(sums.GetValueOrDefault(name), count, ref saturated);
        if (sums.Count > Caps.RollupTop) truncated = true;
        return sums.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Take(Caps.RollupTop).Select(x => new EventNameCount(x.Key, x.Value)).ToList();
    }

    private static long SaturatingAdd(long a, long b, ref bool saturated)
    {
        try { return checked(a + b); }
        catch (OverflowException)
        {
            saturated = true;
            return b > 0 ? long.MaxValue : long.MinValue;
        }
    }

    /// <summary>The middle value; with an even count the mean of the two middle values, rounded down. Null without values.</summary>
    private static long? Median(List<long> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var mid = values.Count / 2;
        if (values.Count % 2 == 1) return values[mid];
        var lo = values[mid - 1];
        var hi = values[mid];
        return (long)Math.Floor(((decimal)lo + hi) / 2);
    }

    /// <summary>Daily turn and optional usage counts, with UTC date bounds matching sessions.</summary>
    public ActivityResult Activity(string? from, string? to, int? limit)
    {
        var take = Caps.ClampLimit(limit, out var clamped);
        var lo = NormalizeBound(from, "--from", endOfDay: false);
        var hi = NormalizeBound(to, "--to", endOfDay: true);

        try { return ActivityCore(lo, hi, take, clamped); }
        catch (Exception ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A daily usage figure is too large to add up (SQLite integer or real overflow); the stored values look wrong.");
        }
    }

    private ActivityResult ActivityCore(string? from, string? to, int take, bool clamped)
    {
        using var conn = Open();
        var hasUsage = ActivityUsageTableReady(conn);
        var credits = hasUsage && CreditsColumnPresent(conn);
        var usageCte = hasUsage
            ? $"""
              , usage_days AS (
                  SELECT strftime('%Y-%m-%d', created_at) AS day, COUNT(*) AS requests,
                         COALESCE(SUM(COALESCE(total_nano_aiu, 0)), 0) AS nano_aiu,
                         {(credits ? CreditsSum : "NULL")} AS credits
                  FROM assistant_usage_events
                  WHERE ($from IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', created_at) >= $from)
                      AND ($to IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', created_at) < $to)
                      AND strftime('%Y-%m-%d', created_at) IS NOT NULL
                  GROUP BY day
              )
              """
            : ", usage_days AS (SELECT NULL AS day, NULL AS requests, NULL AS nano_aiu, NULL AS credits WHERE 0)";
        // Column 5 is the day's request count (0 without usage rows) so a NULL credit sum can be told apart from no rows.
        var usageSelect = hasUsage ? "COALESCE(u.requests, 0), COALESCE(u.nano_aiu, 0), u.credits" : "NULL, NULL, NULL";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH turn_days AS (
                SELECT strftime('%Y-%m-%d', timestamp) AS day, COUNT(*) AS turns, COUNT(DISTINCT session_id) AS chats
                FROM turns
                WHERE ($from IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', timestamp) >= $from)
                  AND ($to IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', timestamp) < $to)
                  AND strftime('%Y-%m-%d', timestamp) IS NOT NULL
                GROUP BY day
            ){usageCte},
            days AS (SELECT day FROM turn_days UNION SELECT day FROM usage_days)
            SELECT d.day, COALESCE(t.turns, 0), COALESCE(t.chats, 0), {usageSelect}
            FROM days d LEFT JOIN turn_days t ON t.day = d.day LEFT JOIN usage_days u ON u.day = d.day
            ORDER BY d.day DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", take + 1);

        var truncated = clamped;
        var days = new List<ActivityDayItem>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (days.Count == take) { truncated = true; break; }
                long? requests = r.IsDBNull(3) ? null : r.GetInt64(3);
                days.Add(new ActivityDayItem(Str(r, 0) ?? "", r.GetInt64(1), r.GetInt64(2),
                    requests, r.IsDBNull(4) ? null : r.GetInt64(4), credits ? ReadCredits(r, 5, requests ?? 0) : null));
            }
        }
        return new ActivityResult(from, to, take, days) { Truncated = truncated };
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

    /// <summary>Newest usage-bearing turn indices, returned ascending, with whole-session totals from the same snapshot.</summary>
    public UsageTurnsResult UsageTurns(string id, int? limit)
    {
        StorePaths.ValidateSessionId(id);
        var take = Caps.ClampLimit(limit, out var clamped);
        try { return UsageTurnsCore(id, take, clamped); }
        catch (Exception ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A usage figure is too large to add up (SQLite integer or real overflow); the stored values look wrong.");
        }
    }

    private UsageTurnsResult UsageTurnsCore(string id, int take, bool clamped)
    {
        using var conn = Open();
        if (ScalarInt(conn, "SELECT COUNT(*) FROM sessions WHERE id = $id", id) == 0)
            throw new ToolkitException(ErrorKind.NotFound, "No matching session row exists.");
        var clampNote = clamped ? " Requested limit was clamped to 100." : "";
        if (!HasColumns(conn, "assistant_usage_events", UsageColumns.Where(x => x != "model").ToArray()))
            return new UsageTurnsResult(false,
                "This store has no usage table (or it has an unexpected shape), so no token figures are available." + clampNote,
                take, null, Array.Empty<UsageTurnSnapshot>()) { Truncated = clamped };

        var credits = CreditsColumnPresent(conn);
        var aggregates = UsageAggregates(credits);
        conn.CreateFunction<string?, string?>("usage_timestamp_utc", UsageTimestampUtc, isDeterministic: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH filtered AS (
                SELECT turn_index, agent_id, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
                       reasoning_tokens, total_nano_aiu, duration_ms,
                       {(credits ? "request_multiplier" : "NULL")} AS request_multiplier,
                       usage_timestamp_utc(created_at) AS usage_at
                FROM assistant_usage_events WHERE session_id = $id
            ), recent AS (
                SELECT turn_index FROM filtered GROUP BY turn_index ORDER BY turn_index DESC LIMIT $n
            )
            SELECT 0 AS row_kind, NULL AS turn_index, {aggregates}, NULL AS peak_input_tokens,
                   NULL AS first_at, NULL AS last_at, NULL AS timestamps_complete,
                   COUNT(DISTINCT turn_index) AS turn_count
            FROM filtered
            UNION ALL
            SELECT 1, turn_index, {aggregates}, MAX(CASE WHEN agent_id IS NULL THEN input_tokens END),
                   MIN(usage_at), MAX(usage_at), COUNT(usage_at) = COUNT(*), NULL
            FROM filtered WHERE turn_index IN (SELECT turn_index FROM recent) GROUP BY turn_index
            ORDER BY row_kind, turn_index
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$n", take);
        using var r = cmd.ExecuteReader();
        r.Read();
        var total = ReadTotals(r, 2, credits);
        if (total.Requests == 0)
            return new UsageTurnsResult(false, "No usage rows are recorded for this session." + clampNote,
                take, null, Array.Empty<UsageTurnSnapshot>()) { Truncated = clamped };
        var truncated = clamped || r.GetInt64(15) > take;
        var turns = new List<UsageTurnSnapshot>();
        while (r.Read())
            turns.Add(new UsageTurnSnapshot(r.GetInt64(1), ReadTotals(r, 2, credits),
                r.IsDBNull(11) ? null : r.GetInt64(11), Str(r, 12), Str(r, 13), r.GetBoolean(14)));
        var note = truncated ? $"The newest {take} usage-bearing turns are returned; total covers all session requests." : null;
        return new UsageTurnsResult(true, note, take, total, turns) { Truncated = truncated };
    }

    private static readonly string[] UsageTimestampFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK",
    };

    private static string? UsageTimestampUtc(string? value) =>
        DateTimeOffset.TryParseExact(value, UsageTimestampFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Token and cost figures of one session, summed from the per-request rows in assistant_usage_events. A store
    /// without that table, or a session without rows, is a normal answer (Recorded = false), not an error.
    /// </summary>
    public UsageResult Usage(string id, bool byTurn)
    {
        try { return UsageCore(id, byTurn); }
        catch (Exception ex) when (IsOverflow(ex))
        {
            throw new ToolkitException(ErrorKind.StoreUnavailable,
                "A usage figure in this session is too large to add up (SQLite integer or real overflow); the stored values look wrong.");
        }
    }

    private static bool IsOverflow(Exception ex) =>
        ex is CreditsOverflowException
        || (ex is SqliteException s && s.Message.Contains("integer overflow", StringComparison.OrdinalIgnoreCase));

    /// <summary>A credit sum that SQLite returned as infinite, or as NULL because it became NaN.</summary>
    private sealed class CreditsOverflowException : Exception { }

    private UsageResult UsageCore(string id, bool byTurn)
    {
        StorePaths.ValidateSessionId(id);
        using var conn = Open();
        EnsureSessionExists(conn, id);

        if (!UsageTableReady(conn))
            return NoUsage(id, "This store has no usage table (or it has an unexpected shape), so no token figures are available.");

        var truncated = false;
        var credits = CreditsColumnPresent(conn);
        var aggregates = UsageAggregates(credits);
        UsageTotals? main = null, sub = null;
        double? mainCredits = null, subCredits = null;   // unrounded, so the total is rounded only once
        string? first = null, last = null;
        long? peakInput = null, lastInput = null;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT (agent_id IS NOT NULL) AS sub, {aggregates}, MIN(created_at), MAX(created_at), MAX(input_tokens)
                FROM assistant_usage_events WHERE session_id = $id GROUP BY sub
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var totals = ReadTotals(r, 1, credits);
                double? raw = credits ? ReadRawCredits(r, 9, totals.Requests) : null;
                if (r.GetInt64(0) == 0) { main = totals; mainCredits = raw; peakInput = r.IsDBNull(12) ? null : r.GetInt64(12); }
                else { sub = totals; subCredits = raw; }
                first = Earlier(first, Str(r, 10));
                last = Later(last, Str(r, 11));
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

        var total = Add(main, sub, mainCredits, subCredits, ref truncated);

        var byModel = new List<UsageModelItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(NULLIF(model, ''), 'unknown') AS m, {aggregates}
                FROM assistant_usage_events WHERE session_id = $id GROUP BY m ORDER BY COUNT(*) DESC, m LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", Caps.StatsTop + 1);
            ReadModels(cmd, byModel, credits, ref truncated);
        }

        List<UsageTurnItem>? turns = null;
        if (byTurn)
        {
            turns = new List<UsageTurnItem>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT turn_index, {aggregates}, MAX(CASE WHEN agent_id IS NULL THEN input_tokens END)
                FROM assistant_usage_events WHERE session_id = $id GROUP BY turn_index ORDER BY turn_index LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$n", Caps.MaxUsageTurns + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (turns.Count == Caps.MaxUsageTurns) { truncated = true; break; }
                turns.Add(new UsageTurnItem(r.GetInt64(0), ReadTotals(r, 1, credits), r.IsDBNull(10) ? null : r.GetInt64(10)));
            }
        }

        return new UsageResult(id, true, null, total, main ?? EmptyTotals(credits), sub ?? EmptyTotals(credits), first, last, peakInput, lastInput, byModel, turns)
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

    /// <summary>Zero totals; credits stay null when the store has no request_multiplier column.</summary>
    private static UsageTotals EmptyTotals(bool credits) => new(0, 0, 0, 0, 0, 0, 0, credits ? 0 : null, 0);

    /// <summary>
    /// Sum of request_multiplier with NULL counted as 0. It is deliberately not wrapped in an outer COALESCE: over at
    /// least one row it is only NULL when SQLite produced NaN, which <see cref="ReadCredits"/> treats as overflow.
    /// </summary>
    private const string CreditsSum = "SUM(COALESCE(request_multiplier, 0))";

    /// <summary>The nine aggregate columns read by <see cref="ReadTotals"/>; the ninth (credits) is NULL without the column.</summary>
    private static string UsageAggregates(bool credits) =>
        "COUNT(*), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0), COALESCE(SUM(cache_read_tokens), 0), " +
        "COALESCE(SUM(cache_write_tokens), 0), COALESCE(SUM(reasoning_tokens), 0), COALESCE(SUM(total_nano_aiu), 0), COALESCE(SUM(duration_ms), 0), " +
        (credits ? CreditsSum : "NULL");

    private static readonly string[] UsageColumns =
    {
        "session_id", "turn_index", "agent_id", "model", "input_tokens", "output_tokens", "cache_read_tokens",
        "cache_write_tokens", "reasoning_tokens", "total_nano_aiu", "duration_ms", "created_at",
    };

    private static readonly string[] ActivityUsageColumns = { "session_id", "created_at", "total_nano_aiu" };

    private static UsageResult NoUsage(string id, string note) =>
        new(id, false, note, null, null, null, null, null, null, null, Array.Empty<UsageModelItem>(), null);

    /// <summary>True when assistant_usage_events exists with every column usage and stats read.</summary>
    private static bool UsageTableReady(SqliteConnection conn) => HasColumns(conn, "assistant_usage_events", UsageColumns);

    /// <summary>True when assistant_usage_events has the columns needed for daily activity.</summary>
    private static bool ActivityUsageTableReady(SqliteConnection conn) => HasColumns(conn, "assistant_usage_events", ActivityUsageColumns);

    /// <summary>True when assistant_usage_events has request_multiplier. It is optional: without it credits are null, never 0.</summary>
    private static bool CreditsColumnPresent(SqliteConnection conn) => HasColumns(conn, "assistant_usage_events", new[] { "request_multiplier" });

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

    /// <summary>Reads the nine aggregate columns that start at <paramref name="o"/> (see <see cref="UsageAggregates"/>).</summary>
    private static UsageTotals ReadTotals(SqliteDataReader r, int o, bool credits)
    {
        var requests = r.GetInt64(o);
        return new(requests, r.GetInt64(o + 1), r.GetInt64(o + 2), r.GetInt64(o + 3), r.GetInt64(o + 4), r.GetInt64(o + 5), r.GetInt64(o + 6),
            credits ? ReadCredits(r, o + 8, requests) : null, r.GetInt64(o + 7));
    }

    /// <summary>
    /// Reads a <see cref="CreditsSum"/> column, rounded to 2 decimals. NULL means no rows (0) or, over at least one row, a
    /// NaN sum; that and an infinite sum throw <see cref="CreditsOverflowException"/> like an integer overflow would.
    /// </summary>
    private static double ReadCredits(SqliteDataReader r, int i, long requests) =>
        Math.Round(ReadRawCredits(r, i, requests), 2, MidpointRounding.AwayFromZero);

    /// <summary>Like <see cref="ReadCredits"/> but unrounded, for sums that are added up further before rounding.</summary>
    private static double ReadRawCredits(SqliteDataReader r, int i, long requests)
    {
        if (r.IsDBNull(i)) return requests == 0 ? 0 : throw new CreditsOverflowException();
        var v = r.GetDouble(i);
        if (!double.IsFinite(v)) throw new CreditsOverflowException();
        return v;
    }

    /// <summary>
    /// Adds two totals; a sum that would not fit in a long stops at long.MaxValue, and a credit sum that is not finite
    /// stops at double.MaxValue; either sets <paramref name="saturated"/>. Credits are added from the unrounded
    /// <paramref name="rawA"/> and <paramref name="rawB"/> and rounded once; they stay null when both are null.
    /// </summary>
    private static UsageTotals Add(UsageTotals? a, UsageTotals? b, double? rawA, double? rawB, ref bool saturated)
    {
        a ??= EmptyTotals(false); b ??= EmptyTotals(false);
        var s = saturated;
        long Sum(long x, long y)
        {
            try { return checked(x + y); }
            catch (OverflowException) { s = true; return long.MaxValue; }
        }
        double? credits = null;
        if (rawA is not null || rawB is not null)
        {
            var c = (rawA ?? 0) + (rawB ?? 0);
            if (double.IsFinite(c)) credits = Math.Round(c, 2, MidpointRounding.AwayFromZero);
            else { s = true; credits = double.MaxValue; }
        }
        var t = new UsageTotals(Sum(a.Requests, b.Requests), Sum(a.InputTokens, b.InputTokens), Sum(a.OutputTokens, b.OutputTokens),
            Sum(a.CacheReadTokens, b.CacheReadTokens), Sum(a.CacheWriteTokens, b.CacheWriteTokens), Sum(a.ReasoningTokens, b.ReasoningTokens),
            Sum(a.NanoAiu, b.NanoAiu), credits, Sum(a.DurationMs, b.DurationMs));
        saturated = s;
        return t;
    }

    private static string? Earlier(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) <= 0 ? a : b;

    private static string? Later(string? a, string? b) => a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;

    /// <summary>Command text must select the model name first, then the nine aggregates, and use LIMIT StatsTop + 1.</summary>
    private static void ReadModels(SqliteCommand cmd, List<UsageModelItem> into, bool credits, ref bool truncated)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (into.Count == Caps.StatsTop) { truncated = true; break; }
            var name = TextUtil.Cap(TextUtil.Collapse(r.GetString(0)), Caps.TitleChars, out var cut);
            truncated |= cut;
            into.Add(new UsageModelItem(name, ReadTotals(r, 1, credits)));
        }
    }

    private static UsageOverview UsageOverviewOf(SqliteConnection conn, ref bool truncated)
    {
        var credits = CreditsColumnPresent(conn);
        var aggregates = UsageAggregates(credits);
        UsageTotals total;
        int sessions;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(DISTINCT session_id), {aggregates} FROM assistant_usage_events";
            using var r = cmd.ExecuteReader();
            r.Read();
            sessions = r.GetInt32(0);
            total = ReadTotals(r, 1, credits);
        }

        var byModel = new List<UsageModelItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(NULLIF(model, ''), 'unknown') AS m, {aggregates}
                FROM assistant_usage_events GROUP BY m ORDER BY COUNT(*) DESC, m LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", Caps.StatsTop + 1);
            ReadModels(cmd, byModel, credits, ref truncated);
        }

        var byMonth = new List<UsageMonthItem>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT COALESCE(NULLIF(substr(created_at, 1, 7), ''), 'unknown') AS k, {aggregates}
                FROM assistant_usage_events GROUP BY k ORDER BY 1 DESC LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", Caps.StatsTop + 1);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (byMonth.Count == Caps.StatsTop) { truncated = true; break; }
                byMonth.Add(new UsageMonthItem(TextUtil.Cap(TextUtil.Collapse(r.GetString(0)), Caps.TitleChars, out _), ReadTotals(r, 1, credits)));
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
        // Same shape as the stored timestamps (milliseconds and a trailing Z), so the text comparison is exact to the millisecond.
        return dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
