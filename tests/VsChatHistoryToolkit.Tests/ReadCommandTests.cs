using Microsoft.Data.Sqlite;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Tests;

public class ReadCommandTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";

    // ---- FTS sanitising ----

    [Theory]
    [InlineData("#include (header) -bar", new[] { "include", "header", "bar" })]
    [InlineData("\"quoted\" AND NOT OR", new[] { "quoted", "AND", "NOT", "OR" })]
    [InlineData("foo_bar baz.qux", new[] { "foo", "bar", "baz", "qux" })]
    public void Tokenize_splits_on_punctuation(string input, string[] expected)
    {
        Assert.Equal(expected, FtsQuery.Tokenize(input, out _));
    }

    [Fact]
    public void ToMatch_quotes_every_term()
    {
        Assert.Equal("\"a\" \"NOT\"", FtsQuery.ToMatch(new[] { "a", "NOT" }));
    }

    [Fact]
    public void Tokenize_caps_term_count_and_length()
    {
        var many = string.Join(' ', Enumerable.Range(0, 30).Select(i => "w" + i));
        Assert.Equal(Caps.MaxSearchTerms, FtsQuery.Tokenize(many, out var cut).Count);
        Assert.True(cut);

        var tokens = FtsQuery.Tokenize(new string('x', 500), out cut);
        Assert.Equal(Caps.MaxTermLength, tokens.Single().Length);
        Assert.True(cut);
    }

    [Fact]
    public void Search_with_only_punctuation_is_a_usage_error()
    {
        using var fx = new FixtureStore();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Search("#-()", null));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    // ---- search ----

    [Fact]
    public void Search_survives_special_characters_and_resolves_the_session()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "why does #include (header) fail", "check the -I flag", "2026-01-05T10:05:00.000Z");

        var result = new ChatStore(fx.Paths).Search("#include (header) -I", null);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(A, hit.SessionId);
        Assert.Equal("turn", hit.SourceType);
        Assert.Equal(Caps.Coverage, result.Coverage);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Search_resolves_checkpoint_rows_by_session_id()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddCheckpoint(A, 1, "Refactor loader", "moved the loader");

        var hit = Assert.Single(new ChatStore(fx.Paths).Search("loader", null).Hits);
        Assert.Equal(A, hit.SessionId);
    }

    [Fact]
    public void Search_limit_sets_truncated_and_hard_cap_applies()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        for (var i = 0; i < 5; i++) fx.AddTurn(A, i, "needle " + i, "reply", $"2026-01-05T10:0{i}:00.000Z");

        var store = new ChatStore(fx.Paths);
        var limited = store.Search("needle", 2);
        Assert.Equal(2, limited.Count);
        Assert.True(limited.Truncated);

        var all = store.Search("needle", 1000);
        Assert.Equal(Caps.MaxLimit, all.Limit);
        Assert.True(all.Truncated);
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public void Search_snippet_is_capped()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddSearchRow("needle " + new string('x', 5000), "turn", A);

        var hit = Assert.Single(new ChatStore(fx.Paths).Search("needle", null).Hits);
        Assert.True(hit.Snippet.Length <= Caps.SnippetChars + 1);
    }

    // ---- sessions ----

    [Fact]
    public void Sessions_title_prefers_yaml_name_then_summary_and_end_is_last_turn()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "summary text", updated: "2026-01-05T10:00:00.000Z");
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.000Z");
        fx.AddTurn(A, 1, "u", "a", "2026-02-01T09:00:00.000Z");
        fx.WriteYaml(A, FixtureStore.SampleYaml(A, "Named chat", userNamed: true));

        fx.AddSession(B, summary: "only summary");
        fx.AddTurn(B, 0, "u", "a", "2026-03-01T00:00:00.000Z");

        var result = new ChatStore(fx.Paths).Sessions(new ChatStore.SessionQuery(null, null, null, null, null, null));

        var b = result.Sessions[0];
        Assert.Equal(B, b.Id);
        Assert.Equal("only summary", b.Title);
        Assert.Equal("summary", b.TitleSource);

        var a = result.Sessions[1];
        Assert.Equal("Named chat", a.Title);
        Assert.Equal("name", a.TitleSource);
        Assert.Equal("2026-02-01T09:00:00.000Z", a.End);
        Assert.Equal("2026-01-05T10:00:00.000Z", a.Start);
        Assert.Equal(2, a.TurnCount);
    }

    [Fact]
    public void Sessions_filters_and_limit()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, repo: "org/alpha", branch: "feature/x", cwd: @"C:\src\alpha");
        fx.AddTurn(A, 0, "u", "a", "2026-01-10T00:00:00.000Z");
        fx.AddSession(B, repo: "org/beta", branch: "main", cwd: @"C:\src\beta");
        fx.AddTurn(B, 0, "u", "a", "2026-02-10T00:00:00.000Z");
        var store = new ChatStore(fx.Paths);

        Assert.Equal(A, Assert.Single(store.Sessions(new(null, null, "feature", null, null, null)).Sessions).Id);
        Assert.Equal(B, Assert.Single(store.Sessions(new("BETA", null, null, null, null, null)).Sessions).Id);
        Assert.Equal(B, Assert.Single(store.Sessions(new(null, @"src\beta", null, null, null, null)).Sessions).Id);
        Assert.Equal(B, Assert.Single(store.Sessions(new(null, null, null, "2026-02-01", null, null)).Sessions).Id);
        Assert.Equal(A, Assert.Single(store.Sessions(new(null, null, null, null, "2026-01-10", null)).Sessions).Id);

        var one = store.Sessions(new(null, null, null, null, null, 1));
        Assert.Single(one.Sessions);
        Assert.True(one.Truncated);
    }

    [Fact]
    public void Sessions_percent_in_filter_is_literal()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, repo: "org/alpha");
        Assert.Empty(new ChatStore(fx.Paths).Sessions(new("%", null, null, null, null, null)).Sessions);
    }

    [Fact]
    public void Sessions_bad_date_is_usage_error()
    {
        using var fx = new FixtureStore();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Sessions(new(null, null, null, "not-a-date", null, null)));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    [Fact]
    public void Sessions_long_title_is_capped_and_flagged()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: new string('z', 1000));
        var result = new ChatStore(fx.Paths).Sessions(new(null, null, null, null, null, null));
        Assert.True(result.Sessions[0].Title!.Length <= Caps.TitleChars + 1);
        Assert.True(result.Truncated);
    }

    // ---- show / solution ----

    [Fact]
    public void Show_reports_solution_from_events()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.AddCheckpoint(A, 1, "First checkpoint");
        fx.WriteEvents(A, new[]
        {
            "{\"type\":\"session.start\",\"data\":{\"note\":\"Solution: Wrong.sln\"}}",
            "{\"type\":\"user.message\",\"data\":{\"content\":\"hi\",\"ide\":\"Active file: a.cs\\nSolution: MyApp.sln\\nProject: Core\"}}",
            "{\"type\":\"user.message\",\"data\":{\"ide\":\"Solution: Other.sln\"}}",
        });

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("MyApp.sln", show.Solution);
        Assert.Equal("events.jsonl", show.SolutionSource);
        Assert.Equal("Original title", show.Session.Title);
        Assert.Equal("First checkpoint", Assert.Single(show.Checkpoints).Title);
        Assert.True(show.WorkspaceYamlPresent);
    }

    [Fact]
    public void Show_solution_is_unknown_when_missing()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEvents(A, new[] { "{\"type\":\"user.message\",\"data\":{\"content\":\"no ide context here\"}}", "{ partial" });

        var show = new ChatStore(fx.Paths).Show(A);
        Assert.Equal("unknown", show.Solution);
        Assert.Equal("none", show.SolutionSource);
    }

    private const string SolutionLine =
        "{\"type\":\"user.message\",\"data\":{\"ide\":\"Solution: Late.sln\"}}";

    [Fact]
    public void Show_solution_absent_after_a_full_scan_is_not_truncated()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEvents(A, new[] { "{\"type\":\"user.message\",\"data\":{\"content\":\"nothing here\"}}" });

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("unknown", show.Solution);
        Assert.Equal("none", show.SolutionSource);
        Assert.False(show.Truncated);
    }

    [Fact]
    public void Show_oversized_line_is_skipped_and_flagged_when_nothing_else_is_found()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEvents(A, new[] { new string('x', SolutionFinder.MaxLineChars + 1) });

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("unknown", show.Solution);
        Assert.Equal("incomplete", show.SolutionSource);
        Assert.True(show.Truncated);
    }

    [Fact]
    public void Show_finds_the_solution_on_a_line_after_an_oversized_one()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEvents(A, new[] { new string('x', SolutionFinder.MaxLineChars + 1), SolutionLine });

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("Late.sln", show.Solution);
        Assert.Equal("events.jsonl", show.SolutionSource);
        Assert.False(show.Truncated);
    }

    [Fact]
    public void Show_line_cap_stops_the_scan_and_says_so()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEvents(A, Enumerable.Repeat("{}", SolutionFinder.MaxLines).Append(SolutionLine));

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("unknown", show.Solution);
        Assert.Equal("incomplete", show.SolutionSource);
        Assert.True(show.Truncated);
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    [InlineData("\r", true)]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r", false)]
    public void Show_finds_the_solution_with_any_line_ending(string eol, bool trailingEol)
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEventsRaw(A, "{}" + eol + SolutionLine + (trailingEol ? eol : ""));

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("Late.sln", show.Solution);
        Assert.False(show.Truncated);
    }

    [Fact]
    public void Show_handles_a_crlf_split_across_the_read_buffer()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        // The solution is on the first line, which is exactly 8191 chars: the CR is the last char of the first
        // 8192-char read and the LF the first of the next. A mishandled boundary would lose or merge the line.
        fx.WriteEventsRaw(A, EventLineOfLength(8191) + "\r\n{}\r\n");

        Assert.Equal("Late.sln", new ChatStore(fx.Paths).Show(A).Solution);
    }

    [Fact]
    public void Show_handles_a_crlf_split_across_the_read_buffer_before_the_solution_line()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEventsRaw(A, new string('x', 8191) + "\r\n" + SolutionLine + "\r\n");

        Assert.Equal("Late.sln", new ChatStore(fx.Paths).Show(A).Solution);
    }

    [Fact]
    public void Show_a_solution_name_over_the_cap_is_shortened_and_flagged()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEventsRaw(A, "{\"type\":\"user.message\",\"data\":{\"ide\":\"Solution: " + new string('s', 300) + "\"}}\r\n");

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.True(show.Solution.Length <= Caps.SnippetChars + 1);
        Assert.StartsWith("sss", show.Solution);
        Assert.True(show.Truncated);
        Assert.Equal("events.jsonl", show.SolutionSource);
    }

    /// <summary>A valid user.message event carrying the solution, padded to exactly <paramref name="length"/> chars.</summary>
    private static string EventLineOfLength(int length)
    {
        const string head = "{\"type\":\"user.message\",\"data\":{\"pad\":\"";
        const string tail = "\",\"ide\":\"Solution: Late.sln\"}}";
        var pad = length - head.Length - tail.Length;
        Assert.True(pad >= 0);
        var line = head + new string('p', pad) + tail;
        Assert.Equal(length, line.Length);
        return line;
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Show_a_file_with_exactly_the_maximum_line_count_is_fully_scanned(string eol)
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEventsRaw(A, string.Concat(Enumerable.Repeat("{}" + eol, SolutionFinder.MaxLines)));

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("none", show.SolutionSource);
        Assert.False(show.Truncated);
    }

    [Fact]
    public void Show_one_line_past_the_maximum_is_incomplete()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEventsRaw(A, string.Concat(Enumerable.Repeat("{}\r\n", SolutionFinder.MaxLines)) + SolutionLine);

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("incomplete", show.SolutionSource);
        Assert.True(show.Truncated);
    }

    [Fact]
    public void Show_a_line_of_exactly_the_maximum_length_is_not_skipped_even_with_crlf()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        // The solution is inside the exactly-at-the-limit line itself, so skipping it would lose the solution.
        fx.WriteEventsRaw(A, EventLineOfLength(SolutionFinder.MaxLineChars) + "\r\n{}");

        var show = new ChatStore(fx.Paths).Show(A);

        Assert.Equal("Late.sln", show.Solution);
        Assert.False(show.Truncated);
    }

    [Fact]
    public void Show_works_without_session_folder_and_fails_for_missing_row()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "just a row");

        var show = new ChatStore(fx.Paths).Show(A);
        Assert.False(show.WorkspaceYamlPresent);
        Assert.Equal("unknown", show.Solution);
        Assert.Equal("summary", show.Session.TitleSource);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Show(B));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Session_ids_that_could_escape_the_folder_are_rejected(string id)
    {
        using var fx = new FixtureStore();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Show(id));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    // ---- turns ----

    [Fact]
    public void Turns_index_range_and_defaults()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        for (var i = 0; i < 15; i++) fx.AddTurn(A, i, "user " + i, "assistant " + i, $"2026-01-05T10:{i:00}:00.000Z");
        var store = new ChatStore(fx.Paths);

        var one = store.Turns(A, 3, null, null);
        Assert.Equal(3, Assert.Single(one.Turns).Index);
        Assert.Equal(15, one.TotalTurns);

        var range = store.Turns(A, null, (2, 4), null);
        Assert.Equal(new long[] { 2, 3, 4 }, range.Turns.Select(t => t.Index));

        var def = store.Turns(A, null, null, null);
        Assert.Equal(Caps.MaxTurnsPerCall, def.Count);

        var wide = store.Turns(A, null, (0, 14), null);
        Assert.Equal(Caps.MaxTurnsPerCall, wide.Count);
        Assert.True(wide.Truncated);
    }

    [Theory]
    [InlineData(0, int.MaxValue)]
    [InlineData(int.MaxValue - 3, int.MaxValue)]
    [InlineData(5, int.MaxValue)]
    public void Turns_extreme_ranges_stay_capped(int from, int to)
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        for (var i = 0; i < 15; i++) fx.AddTurn(A, i, "u" + i, "a", $"2026-01-05T10:{i:00}:00.000Z");

        var result = new ChatStore(fx.Paths).Turns(A, null, (from, to), null);

        Assert.True(result.Count <= Caps.MaxTurnsPerCall);
        if (to - from + 1L > Caps.MaxTurnsPerCall || to - from < 0) Assert.True(result.Truncated);
    }

    [Fact]
    public void Turns_max_chars_truncates_and_flags()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, new string('u', 10_000), "short", "2026-01-05T10:00:00.000Z");

        var result = new ChatStore(fx.Paths).Turns(A, 0, null, 100);
        var t = Assert.Single(result.Turns);
        Assert.True(t.Truncated);
        Assert.True(result.Truncated);
        Assert.True(t.UserMessage!.Length <= 101);
        Assert.Equal(10_000, t.UserMessageChars);
        Assert.Equal("short", t.AssistantResponse);
    }

    [Fact]
    public void Turns_max_chars_above_hard_cap_is_clamped()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, new string('u', 10_000), "a", "2026-01-05T10:00:00.000Z");

        var result = new ChatStore(fx.Paths).Turns(A, 0, null, 1_000_000);
        Assert.True(result.Turns[0].UserMessage!.Length <= Caps.MaxTurnChars + 1);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Turns_total_budget_applies_across_turns()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        for (var i = 0; i < 10; i++)
            fx.AddTurn(A, i, new string('u', 6000), new string('a', 6000), $"2026-01-05T10:{i:00}:00.000Z");

        var result = new ChatStore(fx.Paths).Turns(A, null, (0, 9), 4000);
        var total = result.Turns.Sum(t => (t.UserMessage?.Length ?? 0) + (t.AssistantResponse?.Length ?? 0));
        Assert.True(total <= Caps.TurnsBudgetChars + result.Turns.Count * 2 + 20);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Turns_unknown_session_is_not_found()
    {
        using var fx = new FixtureStore();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Turns(A, null, null, null));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    // ---- checkpoints ----

    [Fact]
    public void Checkpoints_default_is_title_and_overview_and_full_adds_fields()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddCheckpoint(A, 1, "Title one", overview: new string('o', 2000), history: "hist", workDone: "work", technical: "tech", files: "a.cs", next: "next");
        var store = new ChatStore(fx.Paths);

        var brief = Assert.Single(store.Checkpoints(A, full: false).Checkpoints);
        Assert.Equal("Title one", brief.Title);
        Assert.True(brief.Overview!.Length <= Caps.CheckpointOverviewChars + 1);
        Assert.Null(brief.History);
        Assert.True(store.Checkpoints(A, false).Truncated);

        var full = Assert.Single(store.Checkpoints(A, full: true).Checkpoints);
        Assert.Equal("hist", full.History);
        Assert.Equal("work", full.WorkDone);
        Assert.Equal("tech", full.TechnicalDetails);
        Assert.Equal("a.cs", full.ImportantFiles);
        Assert.Equal("next", full.NextSteps);
    }

    // ---- stats ----

    [Fact]
    public void Stats_counts_by_repo_cwd_and_month()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, repo: "org/alpha", cwd: @"C:\a", created: "2026-01-05T10:00:00.000Z");
        fx.AddSession(B, repo: "org/alpha", cwd: @"C:\b", created: "2026-02-05T10:00:00.000Z");
        fx.AddSession("33333333-3333-3333-3333-333333333333", repo: "", cwd: @"C:\b", created: "2026-02-06T10:00:00.000Z");
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:00:00.000Z");

        var stats = new ChatStore(fx.Paths).Stats();

        Assert.Equal(3, stats.TotalSessions);
        Assert.Equal(1, stats.TotalTurns);
        Assert.Equal(2, stats.ByRepository.Single(c => c.Key == "org/alpha").Sessions);
        Assert.Equal(1, stats.ByRepository.Single(c => c.Key == "unknown").Sessions);
        Assert.Equal(2, stats.ByCwd.Single(c => c.Key == @"C:\b").Sessions);
        Assert.Equal(new[] { "2026-02", "2026-01" }, stats.ByMonth.Select(c => c.Key));
    }

    // ---- store access ----

    [Fact]
    public void Schema_mismatch_names_the_found_version()
    {
        using var fx = new FixtureStore(schemaVersion: 7);
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Stats());
        Assert.Equal(ErrorKind.SchemaMismatch, ex.Kind);
        Assert.Contains("7", ex.Message);
        Assert.Equal(ExitCodes.StoreUnavailable, ex.ExitCode);
    }

    [Fact]
    public void Missing_database_is_store_unavailable()
    {
        using var fx = new FixtureStore(createDb: false);
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Stats());
        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
    }

    [Fact]
    public void Read_connection_cannot_write()
    {
        using var fx = new FixtureStore();
        using var conn = StoreConnection.OpenReadOnly(fx.Paths);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(id) VALUES('x')";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Read_sees_rows_that_are_only_in_the_wal_while_a_writer_is_open()
    {
        using var fx = new FixtureStore();
        using var writer = fx.OpenWriter();
        void X(string sql) { using var c = writer.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        X("PRAGMA wal_autocheckpoint=0");
        X($"INSERT INTO sessions(id, summary, created_at) VALUES('{A}', 'recent only in wal', '2026-10-30T11:59:00.000Z')");

        Assert.True(new FileInfo(fx.DbPath + "-wal").Length > 0);

        var sessions = new ChatStore(fx.Paths).Sessions(new(null, null, null, null, null, null));
        Assert.Equal(A, Assert.Single(sessions.Sessions).Id);
    }

    [Fact]
    public void Read_works_while_another_connection_holds_the_write_lock()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        using var writer = fx.OpenWriter();
        using (var c = writer.CreateCommand()) { c.CommandText = "BEGIN IMMEDIATE"; c.ExecuteNonQuery(); }

        Assert.Equal(1, new ChatStore(fx.Paths).Stats().TotalSessions);
    }
}
