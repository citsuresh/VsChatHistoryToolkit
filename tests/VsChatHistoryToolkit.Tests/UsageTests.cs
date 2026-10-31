using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class UsageTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";

    private static FixtureStore StoreWithUsage()
    {
        var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddSession(B);
        // A: two main requests on turn 0, one on turn 1, plus a subagent request on turn 1 with another model.
        fx.AddUsage(A, 0, "model-x", 1000, 50, cacheRead: 600, cacheWrite: 100, reasoning: 10, nanoAiu: 5_000_000, durationMs: 200, createdAt: "2026-01-05T10:00:00.000Z");
        fx.AddUsage(A, 0, "model-x", 1500, 70, cacheRead: 900, cacheWrite: 0, reasoning: 20, nanoAiu: 7_000_000, durationMs: 300, createdAt: "2026-01-05T10:01:00.000Z");
        fx.AddUsage(A, 1, "model-x", 2000, 90, cacheRead: 1800, cacheWrite: 0, reasoning: 30, nanoAiu: 9_000_000, durationMs: 400, createdAt: "2026-02-01T09:00:00.000Z");
        fx.AddUsage(A, 1, "model-y", 300, 20, nanoAiu: 1_000_000, durationMs: 50, createdAt: "2026-02-01T09:00:30.000Z", agentId: "agent-1");
        fx.AddUsage(B, 0, "model-y", 10, 1, nanoAiu: null, reasoning: null, createdAt: "2026-02-03T09:00:00.000Z");
        return fx;
    }

    [Fact]
    public void Usage_sums_total_main_and_subagent_requests()
    {
        using var fx = StoreWithUsage();

        var u = new ChatStore(fx.Paths).Usage(A, byTurn: false);

        Assert.True(u.Recorded);
        Assert.Equal(4, u.Total!.Requests);
        Assert.Equal(3, u.Main!.Requests);
        Assert.Equal(1, u.Subagents!.Requests);
        Assert.Equal(1000 + 1500 + 2000 + 300, u.Total.InputTokens);
        Assert.Equal(50 + 70 + 90 + 20, u.Total.OutputTokens);
        Assert.Equal(600 + 900 + 1800, u.Total.CacheReadTokens);
        Assert.Equal(100, u.Total.CacheWriteTokens);
        Assert.Equal(60, u.Total.ReasoningTokens);
        Assert.Equal(22_000_000, u.Total.NanoAiu);
        Assert.Equal(950, u.Total.DurationMs);
        Assert.Equal(1000 + 1500 + 2000, u.Main.InputTokens);
        Assert.Equal(300, u.Subagents.InputTokens);
        Assert.Equal("2026-01-05T10:00:00.000Z", u.FirstAt);
        Assert.Equal("2026-02-01T09:00:30.000Z", u.LastAt);
        Assert.Null(u.ByTurn);
        Assert.False(u.Truncated);
        Assert.Equal(Caps.Coverage, u.Coverage);
    }

    [Fact]
    public void Usage_reports_peak_and_last_prompt_size_of_main_requests_only()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        // Inserted out of time order, so "last" must follow created_at rather than the row id.
        fx.AddUsage(A, 1, "m", 3000, 1, createdAt: "2026-01-05T10:02:00.000Z");
        fx.AddUsage(A, 0, "m", 5000, 1, createdAt: "2026-01-05T10:01:00.000Z");
        fx.AddUsage(A, 1, "m", 9000, 1, createdAt: "2026-01-05T10:03:00.000Z", agentId: "agent-1");

        var u = new ChatStore(fx.Paths).Usage(A, byTurn: false);

        Assert.Equal(5000, u.PeakInputTokens);
        Assert.Equal(3000, u.LastInputTokens);
    }

    [Fact]
    public void Usage_last_prompt_size_breaks_a_created_at_tie_by_the_latest_row()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 700, 1, createdAt: "2026-01-05T10:00:00.000Z");
        fx.AddUsage(A, 0, "m", 200, 1, createdAt: "2026-01-05T10:00:00.000Z");

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.Equal(700, u.PeakInputTokens);
        Assert.Equal(200, u.LastInputTokens);
    }

    [Fact]
    public void Usage_works_with_a_table_without_an_id_column()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable(withId: false);
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 700, 1, createdAt: "2026-01-05T10:00:00.000Z");
        fx.AddUsage(A, 0, "m", 200, 1, createdAt: "2026-01-05T10:00:00.000Z");

        var u = new ChatStore(fx.Paths).Usage(A, true);
        var s = new ChatStore(fx.Paths).Stats();

        Assert.True(u.Recorded);
        Assert.Equal(700, u.PeakInputTokens);
        Assert.Equal(200, u.LastInputTokens);
        Assert.NotNull(s.Usage);
        Assert.Equal(2, s.Usage!.Total.Requests);
    }

    [Fact]
    public void Usage_prompt_size_is_null_without_main_requests()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 400, 1, agentId: "agent-1");

        var u = new ChatStore(fx.Paths).Usage(A, true);

        Assert.True(u.Recorded);
        Assert.Null(u.PeakInputTokens);
        Assert.Null(u.LastInputTokens);
        Assert.Null(u.ByTurn![0].PeakInputTokens);
        var json = JsonOut.Serialize(u);
        Assert.DoesNotContain("peak_input_tokens", json);
        Assert.DoesNotContain("last_input_tokens", json);
    }

    [Fact]
    public void Usage_prompt_size_is_absent_when_nothing_is_recorded()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.False(u.Recorded);
        Assert.Null(u.PeakInputTokens);
        Assert.Null(u.LastInputTokens);
    }

    [Fact]
    public void Usage_by_turn_reports_the_peak_main_prompt_of_each_turn()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 4000, 1);
        fx.AddUsage(A, 0, "m", 5000, 1);
        fx.AddUsage(A, 0, "m", 8000, 1, agentId: "agent-1");
        fx.AddUsage(A, 1, "m", 6000, 1, agentId: "agent-1");
        fx.AddUsage(A, 2, "m", 2500, 1);

        var u = new ChatStore(fx.Paths).Usage(A, byTurn: true);

        Assert.Equal(new long?[] { 5000, null, 2500 }, u.ByTurn!.Select(t => t.PeakInputTokens));
        Assert.Equal(5000, u.PeakInputTokens);
    }

    [Fact]
    public void Usage_total_stops_at_the_maximum_and_is_flagged_when_main_plus_subagents_overflow()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        // Different models so no single SQL SUM overflows; only the main + subagent addition does.
        fx.AddUsage(A, 0, "model-a", 1, 1, nanoAiu: long.MaxValue - 1);
        fx.AddUsage(A, 0, "model-b", 1, 1, nanoAiu: 5, agentId: "agent-1");

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.Equal(long.MaxValue, u.Total!.NanoAiu);
        Assert.Equal(2, u.Total.Requests);
        Assert.Equal(long.MaxValue - 1, u.Main!.NanoAiu);
        Assert.Equal(5, u.Subagents!.NanoAiu);
        Assert.True(u.Truncated);
    }

    [Fact]
    public void Usage_reports_a_clear_error_and_stats_drops_the_section_when_a_sum_overflows_in_sqlite()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: long.MaxValue - 1);
        fx.AddUsage(A, 1, "m", 1, 1, nanoAiu: 5);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Usage(A, false));
        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
        Assert.Contains("too large", ex.Message);

        var stats = new ChatStore(fx.Paths).Stats();
        Assert.Null(stats.Usage);
        Assert.True(stats.Truncated);
        Assert.Equal(1, stats.TotalSessions);
    }

    [Fact]
    public void Usage_does_not_mix_sessions_and_treats_null_cost_as_zero()
    {
        using var fx = StoreWithUsage();

        var u = new ChatStore(fx.Paths).Usage(B, byTurn: false);

        Assert.Equal(1, u.Total!.Requests);
        Assert.Equal(10, u.Total.InputTokens);
        Assert.Equal(0, u.Total.NanoAiu);
        Assert.Equal(0, u.Total.ReasoningTokens);
    }

    [Fact]
    public void Usage_splits_by_model_busiest_first()
    {
        using var fx = StoreWithUsage();

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.Equal(new[] { "model-x", "model-y" }, u.ByModel.Select(m => m.Model));
        Assert.Equal(3, u.ByModel[0].Totals.Requests);
        Assert.Equal(1, u.ByModel[1].Totals.Requests);
    }

    [Fact]
    public void Usage_by_turn_lists_turns_in_order()
    {
        using var fx = StoreWithUsage();

        var u = new ChatStore(fx.Paths).Usage(A, byTurn: true);

        Assert.Equal(new long[] { 0, 1 }, u.ByTurn!.Select(t => t.TurnIndex));
        Assert.Equal(2, u.ByTurn![0].Totals.Requests);
        Assert.Equal(2, u.ByTurn![1].Totals.Requests);
        Assert.Equal(2300, u.ByTurn![1].Totals.InputTokens);
    }

    [Fact]
    public void Usage_by_turn_is_capped_and_flagged()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        for (var i = 0; i < Caps.MaxUsageTurns + 5; i++) fx.AddUsage(A, i, "m", 1, 1);

        var u = new ChatStore(fx.Paths).Usage(A, byTurn: true);

        Assert.Equal(Caps.MaxUsageTurns, u.ByTurn!.Count);
        Assert.True(u.Truncated);
        Assert.Equal(Caps.MaxUsageTurns + 5, u.Total!.Requests);
    }

    [Fact]
    public void Usage_by_model_is_capped_and_flagged()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        for (var i = 0; i < Caps.StatsTop + 3; i++) fx.AddUsage(A, 0, "model-" + i.ToString("D3"), 1, 1);

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.Equal(Caps.StatsTop, u.ByModel.Count);
        Assert.True(u.Truncated);
    }

    [Fact]
    public void Usage_for_a_session_without_rows_is_a_normal_answer()
    {
        using var fx = StoreWithUsage();
        const string C = "33333333-3333-3333-3333-333333333333";
        fx.AddSession(C);

        var u = new ChatStore(fx.Paths).Usage(C, false);

        Assert.False(u.Recorded);
        Assert.Null(u.Total);
        Assert.Contains("No usage rows", u.Note);
        Assert.Equal(Caps.Coverage, u.Coverage);
    }

    [Fact]
    public void Usage_without_the_table_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.False(u.Recorded);
        Assert.Contains("no usage table", u.Note);
    }

    [Fact]
    public void Usage_with_a_table_missing_a_needed_column_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable(dropColumn: true);
        fx.AddSession(A);

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.False(u.Recorded);
    }

    [Fact]
    public void Usage_for_an_unknown_session_is_not_found()
    {
        using var fx = StoreWithUsage();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Usage("99999999-9999-9999-9999-999999999999", false));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Usage_rejects_a_malformed_id()
    {
        using var fx = StoreWithUsage();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Usage("..\\evil", false));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    [Fact]
    public void Usage_does_not_write_to_the_store()
    {
        using var fx = StoreWithUsage();
        var before = File.ReadAllBytes(fx.DbPath);
        new ChatStore(fx.Paths).Usage(A, true);
        Assert.Equal(before, File.ReadAllBytes(fx.DbPath));
    }

    // ---- credits ----

    /// <summary>A: turn 0 has 11 main requests of 1.0, turn 1 has 4 main requests of 15.0 and 2 subagent requests of 1.0 on another model.</summary>
    private static FixtureStore StoreWithCredits()
    {
        var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        for (var i = 0; i < 11; i++) fx.AddUsage(A, 0, "model-x", 1, 1, multiplier: 1.0);
        for (var i = 0; i < 4; i++) fx.AddUsage(A, 1, "model-x", 1, 1, multiplier: 15.0, createdAt: "2026-02-01T09:00:00.000Z");
        for (var i = 0; i < 2; i++) fx.AddUsage(A, 1, "model-y", 1, 1, multiplier: 1.0, agentId: "agent-1", createdAt: "2026-02-01T09:00:00.000Z");
        return fx;
    }

    [Fact]
    public void Usage_credits_sum_request_multiplier_for_total_main_subagents_models_and_turns()
    {
        using var fx = StoreWithCredits();

        var u = new ChatStore(fx.Paths).Usage(A, byTurn: true);

        Assert.True(u.Recorded);
        Assert.Equal(73.0, u.Total!.Credits);
        Assert.Equal(71.0, u.Main!.Credits);
        Assert.Equal(2.0, u.Subagents!.Credits);
        Assert.Equal(new double?[] { 71.0, 2.0 }, u.ByModel.Select(m => m.Totals.Credits));
        Assert.Equal(new double?[] { 11.0, 62.0 }, u.ByTurn!.Select(t => t.Totals.Credits));
        Assert.False(u.Truncated);
    }

    [Fact]
    public void Usage_credits_count_a_null_multiplier_as_zero_and_round_to_two_decimals()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddSession(B);
        fx.AddUsage(A, 0, "m", 1, 1, multiplier: 0.125);
        fx.AddUsage(A, 0, "m", 1, 1);
        fx.AddUsage(A, 1, "m", 1, 1, multiplier: 1.0 / 3);
        fx.AddUsage(B, 0, "m", 1, 1);

        var store = new ChatStore(fx.Paths);
        var a = store.Usage(A, byTurn: true);
        var b = store.Usage(B, byTurn: false);

        Assert.Equal(0.46, a.Total!.Credits);
        Assert.Equal(new double?[] { 0.13, 0.33 }, a.ByTurn!.Select(t => t.Totals.Credits));
        Assert.Equal(0.0, a.Subagents!.Credits);
        Assert.Equal(0.0, b.Total!.Credits);
        Assert.Contains("\"credits\":0", JsonOut.Serialize(b));
    }

    [Fact]
    public void Usage_and_stats_omit_credits_when_the_table_has_no_request_multiplier_column()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable(withMultiplier: false);
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 10, 1, nanoAiu: 5);
        fx.AddUsage(A, 1, "m", 20, 1, nanoAiu: 6, agentId: "agent-1");

        var store = new ChatStore(fx.Paths);
        var u = store.Usage(A, byTurn: true);
        var s = store.Stats();

        Assert.True(u.Recorded);
        Assert.Equal(2, u.Total!.Requests);
        Assert.Equal(11, u.Total.NanoAiu);
        Assert.Null(u.Total.Credits);
        Assert.Null(u.Main!.Credits);
        Assert.Null(u.Subagents!.Credits);
        Assert.All(u.ByModel, m => Assert.Null(m.Totals.Credits));
        Assert.All(u.ByTurn!, t => Assert.Null(t.Totals.Credits));
        Assert.DoesNotContain("credits", JsonOut.Serialize(u));

        Assert.NotNull(s.Usage);
        Assert.Null(s.Usage!.Total.Credits);
        Assert.DoesNotContain("credits", JsonOut.Serialize(s));

        Assert.DoesNotContain("credits", Run(fx, "usage", A, "--text").Out);
    }

    [Fact]
    public void Usage_main_only_session_without_the_column_keeps_subagent_credits_null()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable(withMultiplier: false);
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 10, 1);

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.Equal(0, u.Subagents!.Requests);
        Assert.Null(u.Subagents.Credits);
    }

    [Fact]
    public void Usage_total_credits_are_rounded_once_and_match_the_stats_total()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "model-a", 1, 1, multiplier: 0.125);
        fx.AddUsage(A, 0, "model-b", 1, 1, multiplier: 0.125, agentId: "agent-1");

        var store = new ChatStore(fx.Paths);
        var u = store.Usage(A, false);
        var s = store.Stats();

        Assert.Equal(0.13, u.Main!.Credits);
        Assert.Equal(0.13, u.Subagents!.Credits);
        Assert.Equal(0.25, u.Total!.Credits);
        Assert.Equal(s.Usage!.Total.Credits, u.Total.Credits);
    }

    [Fact]
    public void Usage_credits_overflow_in_sqlite_is_a_clear_error_and_stats_drops_the_section()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 1, 1, multiplier: double.MaxValue);
        fx.AddUsage(A, 1, "m", 1, 1, multiplier: double.MaxValue);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Usage(A, false));
        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
        Assert.Contains("too large", ex.Message);

        var stats = new ChatStore(fx.Paths).Stats();
        Assert.Null(stats.Usage);
        Assert.True(stats.Truncated);
    }

    [Fact]
    public void Usage_credits_that_cancel_to_not_a_number_in_sqlite_are_an_overflow_error()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        // SQLite turns a NaN sum (+infinity plus -infinity) into NULL; over stored rows that must not read as 0 credits.
        fx.AddUsage(A, 0, "m", 1, 1, multiplier: double.PositiveInfinity);
        fx.AddUsage(A, 0, "m", 1, 1, multiplier: double.NegativeInfinity);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Usage(A, false));

        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
    }

    [Fact]
    public void Usage_total_credits_stop_at_the_maximum_and_are_flagged_when_main_plus_subagents_overflow()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        // Different models so no single SQL SUM overflows; only the main + subagent addition does.
        fx.AddUsage(A, 0, "model-a", 1, 1, multiplier: double.MaxValue);
        fx.AddUsage(A, 0, "model-b", 1, 1, multiplier: double.MaxValue, agentId: "agent-1");

        var u = new ChatStore(fx.Paths).Usage(A, false);

        Assert.Equal(double.MaxValue, u.Total!.Credits);
        Assert.Equal(double.MaxValue, u.Main!.Credits);
        Assert.True(u.Truncated);
        JsonOut.Serialize(u);   // must not throw on a non-finite number
    }

    [Fact]
    public void Stats_includes_credits_in_total_models_and_months()
    {
        using var fx = StoreWithCredits();

        var s = new ChatStore(fx.Paths).Stats();

        Assert.Equal(73.0, s.Usage!.Total.Credits);
        Assert.Equal(new double?[] { 71.0, 2.0 }, s.Usage.ByModel.Select(m => m.Totals.Credits));
        Assert.Equal(new double?[] { 62.0, 11.0 }, s.Usage.ByMonth.Select(m => m.Totals.Credits));
    }

    [Fact]
    public void Cli_usage_json_and_text_show_credits()
    {
        using var fx = StoreWithCredits();

        var (exit, output) = Run(fx, "usage", A, "--by-turn");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(73.0, root.GetProperty("total").GetProperty("credits").GetDouble());
        Assert.Equal(71.0, root.GetProperty("main").GetProperty("credits").GetDouble());
        Assert.Equal(2.0, root.GetProperty("subagents").GetProperty("credits").GetDouble());
        Assert.Equal(62.0, root.GetProperty("by_turn")[1].GetProperty("totals").GetProperty("credits").GetDouble());

        var text = Run(fx, "usage", A, "--text").Out;
        Assert.Contains("main:      15 requests", text);
        Assert.Contains(", credits 71.0,", text);
        Assert.Contains(", credits 2.0,", text);
        Assert.Contains("matched the chat footers on 7 of 7 turns", text);

        Assert.Contains("total: 17 requests", Run(fx, "stats", "--text").Out);
        Assert.Contains("credits 73.0", Run(fx, "stats", "--text").Out);
    }

    // ---- stats ----

    [Fact]
    public void Stats_includes_usage_overview_when_the_table_exists()
    {
        using var fx = StoreWithUsage();

        var s = new ChatStore(fx.Paths).Stats();

        Assert.NotNull(s.Usage);
        Assert.Equal(2, s.Usage!.SessionsWithUsage);
        Assert.Equal(5, s.Usage.Total.Requests);
        Assert.Equal(2, s.Usage.ByModel.Count);
        Assert.Equal(new[] { "2026-02", "2026-01" }, s.Usage.ByMonth.Select(m => m.Month));
        Assert.Equal(3, s.Usage.ByMonth[0].Totals.Requests);
        Assert.Equal(2, s.Usage.ByMonth[1].Totals.Requests);
    }

    [Fact]
    public void Stats_has_no_usage_section_without_the_table()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);

        var s = new ChatStore(fx.Paths).Stats();

        Assert.Null(s.Usage);
        Assert.DoesNotContain("\"usage\"", JsonOut.Serialize(s));
    }

    // ---- CLI ----

    private static (int Exit, string Out) Run(FixtureStore fx, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), o, e);
        return (exit, o.ToString());
    }

    [Fact]
    public void Cli_usage_prints_snake_case_json()
    {
        using var fx = StoreWithUsage();

        var (exit, output) = Run(fx, "usage", A, "--by-turn");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.Equal(4, root.GetProperty("total").GetProperty("requests").GetInt64());
        Assert.Equal(22_000_000, root.GetProperty("total").GetProperty("nano_aiu").GetInt64());
        Assert.Equal(2, root.GetProperty("by_turn").GetArrayLength());
        Assert.Equal(2000, root.GetProperty("peak_input_tokens").GetInt64());
        Assert.Equal(2000, root.GetProperty("last_input_tokens").GetInt64());
        Assert.Equal(1500, root.GetProperty("by_turn")[0].GetProperty("peak_input_tokens").GetInt64());
    }

    [Fact]
    public void Cli_usage_text_mentions_coverage_and_the_cost_unit()
    {
        using var fx = StoreWithUsage();

        var (exit, output) = Run(fx, "usage", A, "--text");

        Assert.Equal(0, exit);
        Assert.Contains("coverage: session-store.db only", output);
        Assert.Contains("nano_aiu", output);
        Assert.Contains("subagents:", output);
        Assert.Contains("main prompt size: peak 2000, last 2000 input tokens", output);
    }

    [Theory]
    [InlineData("2026-01-05T10:05:00.400Z", null, true)]    // --from just before the turn: included
    [InlineData("2026-01-05T10:05:00.900Z", null, false)]   // --from after the turn by 400 ms: excluded (used to be truncated to :00 and included)
    [InlineData(null, "2026-01-05T10:05:00.900Z", true)]    // --to after the turn: included
    [InlineData(null, "2026-01-05T10:05:00.500Z", false)]   // --to is exclusive: a turn at exactly that instant is excluded
    public void Date_bounds_keep_fractional_seconds(string? from, string? to, bool expected)
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.500Z");

        var r = new ChatStore(fx.Paths).Sessions(new ChatStore.SessionQuery(null, null, null, from, to, null));

        Assert.Equal(expected, r.Sessions.Count == 1);
    }

    [Fact]
    public void Cli_usage_needs_exactly_one_id()
    {
        using var fx = StoreWithUsage();
        Assert.Equal(ExitCodes.Usage, Run(fx, "usage").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "usage", A, B).Exit);
    }

    [Fact]
    public void Cli_usage_for_an_unknown_session_exits_with_not_found()
    {
        using var fx = StoreWithUsage();
        Assert.Equal(ExitCodes.NotFound, Run(fx, "usage", "99999999-9999-9999-9999-999999999999").Exit);
    }

    [Fact]
    public void Cli_stats_text_shows_usage_lines()
    {
        using var fx = StoreWithUsage();

        var (exit, output) = Run(fx, "stats", "--text");

        Assert.Equal(0, exit);
        Assert.Contains("usage: 2 session(s) with usage rows", output);
        Assert.Contains("model model-x:", output);
    }

    [Fact]
    public void Text_output_cleans_control_characters_in_model_names()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "evil\u001b[31mmodel\nFAKE: line", 1, 1);

        var (_, output) = Run(fx, "usage", A, "--text");

        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFAKE:", output, StringComparison.Ordinal);
    }
}
