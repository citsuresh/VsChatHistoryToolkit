using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class ActivityTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void Activity_groups_turns_chats_and_usage_by_utc_day()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddSession(B);
        fx.AddSession(C);
        fx.AddTurn(A, 0, "u", "a", "2026-01-01T10:00:00.000Z");
        fx.AddTurn(A, 1, "u", "a", "2026-01-01T11:00:00.000Z");
        fx.AddTurn(B, 0, "u", "a", "2026-01-02T00:30:00+02:00");
        fx.AddTurn(C, 0, "u", "a", "2026-01-02T00:00:00.000Z");
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: 10, createdAt: "2026-01-01T08:00:00.000Z");
        fx.AddUsage(B, 0, "m", 1, 1, nanoAiu: 2, createdAt: "2026-01-02T00:30:00+02:00");
        fx.AddUsage(A, 1, "m", 1, 1, nanoAiu: 5, createdAt: "2026-01-03T08:00:00.000Z");

        var result = new ChatStore(fx.Paths).Activity(null, null, null);

        Assert.Equal(new[] { "2026-01-03", "2026-01-02", "2026-01-01" }, result.Days.Select(d => d.Day));
        Assert.Equal(new ActivityDayItem("2026-01-03", 0, 0, 1, 5), result.Days[0]);
        Assert.Equal(new ActivityDayItem("2026-01-02", 1, 1, 0, 0), result.Days[1]);
        Assert.Equal(new ActivityDayItem("2026-01-01", 3, 2, 2, 12), result.Days[2]);
        Assert.False(result.Truncated);
        Assert.Equal(Caps.Coverage, result.Coverage);
    }

    [Fact]
    public void Activity_filters_date_bounds_and_date_only_to_is_inclusive()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-01T23:59:59.999Z");
        fx.AddTurn(A, 1, "u", "a", "2026-01-02T00:00:00.000Z");
        fx.AddTurn(A, 2, "u", "a", "2026-01-03T00:00:00.000Z");
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: 4, createdAt: "2026-01-02T12:00:00.000Z");

        var result = new ChatStore(fx.Paths).Activity("2026-01-01", "2026-01-02", null);

        Assert.Equal(new[] { "2026-01-02", "2026-01-01" }, result.Days.Select(d => d.Day));
        Assert.Equal("2026-01-01T00:00:00.000Z", result.From);
        Assert.Equal("2026-01-03T00:00:00.000Z", result.To);
        Assert.Equal(1, result.Days[0].Requests);
        Assert.Equal(4, result.Days[0].NanoAiu);
    }

    [Fact]
    public void Activity_reads_usage_from_only_the_columns_it_needs()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.Exec("CREATE TABLE assistant_usage_events(session_id TEXT, created_at TEXT, total_nano_aiu INTEGER)");
        fx.Exec("INSERT INTO assistant_usage_events VALUES($s, $c, $n)", ("$s", A), ("$c", "2026-01-02T00:00:00.000Z"), ("$n", 9));

        var result = new ChatStore(fx.Paths).Activity(null, null, null);

        Assert.Equal(new ActivityDayItem("2026-01-02", 0, 0, 1, 9), Assert.Single(result.Days));
    }

    [Fact]
    public void Activity_omits_usage_metrics_without_a_usable_usage_table()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-01T10:00:00.000Z");

        var result = new ChatStore(fx.Paths).Activity(null, null, null);
        var json = JsonOut.Serialize(result);

        Assert.Null(result.Days[0].Requests);
        Assert.Null(result.Days[0].NanoAiu);
        Assert.DoesNotContain("\"requests\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"nano_aiu\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Activity_caps_days_and_flags_a_cut_or_clamped_limit()
    {
        using var fx = new FixtureStore();
        for (var i = 0; i < Caps.MaxLimit + 1; i++)
        {
            var id = $"session{i:000}";
            fx.AddSession(id);
            fx.AddTurn(id, 0, "u", "a", new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        }
        var store = new ChatStore(fx.Paths);

        var limited = store.Activity(null, null, 2);
        var clamped = store.Activity(null, null, 1000);

        Assert.Equal(2, limited.Count);
        Assert.Equal(new[] { "2026-04-11", "2026-04-10" }, limited.Days.Select(d => d.Day));
        Assert.True(limited.Truncated);
        Assert.Equal(Caps.MaxLimit, clamped.Limit);
        Assert.Equal(Caps.MaxLimit, clamped.Count);
        Assert.True(clamped.Truncated);
    }

    [Fact]
    public void Activity_rejects_bad_dates_and_nonpositive_limits()
    {
        using var fx = new FixtureStore();
        var store = new ChatStore(fx.Paths);

        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => store.Activity("yesterday", null, null)).Kind);
        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => store.Activity(null, null, 0)).Kind);
    }

    [Fact]
    public void Activity_reports_sqlite_overflow_for_daily_costs()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: long.MaxValue);
        fx.AddUsage(A, 1, "m", 1, 1, nanoAiu: 1);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Activity(null, null, null));

        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Cli_activity_json_has_coverage_and_text_has_daily_totals()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-01T10:00:00.000Z");
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: 7, createdAt: "2026-01-01T10:00:00.000Z");

        var (exit, output) = Run(fx, "activity", "--from", "2026-01-01");
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("activity", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        var day = root.GetProperty("days")[0];
        Assert.Equal("2026-01-01", day.GetProperty("day").GetString());
        Assert.Equal(1, day.GetProperty("turns").GetInt64());
        Assert.Equal(1, day.GetProperty("chats").GetInt64());
        Assert.Equal(1, day.GetProperty("requests").GetInt64());
        Assert.Equal(7, day.GetProperty("nano_aiu").GetInt64());

        var text = Run(fx, "activity", "--text").Output;
        Assert.Contains("2026-01-01: 1 turns, 1 chats, 1 requests, nano_aiu 7", text);
        Assert.Contains("coverage: session-store.db only", text);
    }

    [Fact]
    public void Cli_activity_rejects_bad_arguments()
    {
        using var fx = new FixtureStore();
        Assert.Equal(ExitCodes.Usage, Run(fx, "activity", "extra").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "activity", "--from", "bad-date").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "activity", "--limit", "0").Exit);
    }

    private static (int Exit, string Output) Run(FixtureStore fx, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), stdout, stderr);
        return (exit, stdout.ToString());
    }
}
