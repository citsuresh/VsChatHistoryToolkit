using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class UsageTopTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";

    /// <summary>A: most cost, B: most tokens, C: most requests, so each --by gives a different leader.</summary>
    private static FixtureStore StoreWithUsage()
    {
        var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A, summary: "chat a");
        fx.AddSession(B, summary: "chat b");
        fx.AddSession(C, summary: "chat c");
        fx.AddUsage(A, 0, "m", 100, 10, cacheRead: 50, reasoning: 5, nanoAiu: 9_000, createdAt: "2026-01-05T10:00:00.000Z");
        fx.AddUsage(B, 0, "m", 5000, 500, nanoAiu: 1_000, createdAt: "2026-01-10T10:00:00.000Z");
        fx.AddUsage(C, 0, "m", 10, 1, nanoAiu: 100, createdAt: "2026-01-20T10:00:00.000Z");
        fx.AddUsage(C, 1, "m", 10, 1, nanoAiu: 100, createdAt: "2026-01-20T11:00:00.000Z");
        fx.AddUsage(C, 2, "m", 10, 1, nanoAiu: null, createdAt: "2026-01-31T23:59:59.000Z", agentId: "agent-1");
        return fx;
    }

    [Fact]
    public void Usage_top_ranks_by_aiu_by_default()
    {
        using var fx = StoreWithUsage();

        var r = new ChatStore(fx.Paths).UsageTop(null, null, null, null);

        Assert.True(r.Recorded);
        Assert.Equal("aiu", r.By);
        Assert.Equal(new[] { A, B, C }, r.Sessions.Select(s => s.SessionId));
        var a = r.Sessions[0];
        Assert.Equal(1, a.Requests);
        Assert.Equal(100, a.InputTokens);
        Assert.Equal(10, a.OutputTokens);
        Assert.Equal(50, a.CacheReadTokens);
        Assert.Equal(5, a.ReasoningTokens);
        Assert.Equal(9_000, a.NanoAiu);
        Assert.Equal("2026-01-05T10:00:00.000Z", a.LastAt);
        Assert.Equal("chat a", a.Title);
        Assert.Equal("summary", a.TitleSource);
        Assert.Equal(Caps.DefaultLimit, r.Limit);
        Assert.False(r.Truncated);
        Assert.Equal(Caps.Coverage, r.Coverage);
    }

    [Fact]
    public void Usage_top_ranks_by_tokens_and_by_requests()
    {
        using var fx = StoreWithUsage();
        var store = new ChatStore(fx.Paths);

        Assert.Equal(new[] { B, A, C }, store.UsageTop(null, null, "tokens", null).Sessions.Select(s => s.SessionId));
        Assert.Equal(new[] { C, A, B }, store.UsageTop(null, null, "requests", null).Sessions.Select(s => s.SessionId));
        Assert.Equal("requests", store.UsageTop(null, null, " Requests ", null).By);
    }

    [Fact]
    public void Usage_top_counts_subagent_requests_and_treats_null_cost_as_zero()
    {
        using var fx = StoreWithUsage();

        var c = new ChatStore(fx.Paths).UsageTop(null, null, "requests", null).Sessions[0];

        Assert.Equal(C, c.SessionId);
        Assert.Equal(3, c.Requests);
        Assert.Equal(200, c.NanoAiu);
        Assert.Equal("2026-01-31T23:59:59.000Z", c.LastAt);
    }

    [Fact]
    public void Usage_top_breaks_ties_by_session_id()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(C);
        fx.AddSession(A);
        fx.AddSession(B);
        fx.AddUsage(C, 0, "m", 1, 1, nanoAiu: 5);
        fx.AddUsage(B, 0, "m", 1, 1, nanoAiu: 5);
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: 5);

        var r = new ChatStore(fx.Paths).UsageTop(null, null, null, null);

        Assert.Equal(new[] { A, B, C }, r.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Usage_top_filters_each_request_by_created_at_and_a_date_only_to_is_inclusive()
    {
        using var fx = StoreWithUsage();
        var store = new ChatStore(fx.Paths);

        var r = store.UsageTop("2026-01-10", "2026-01-20", "requests", null);

        Assert.Equal(new[] { C, B }, r.Sessions.Select(s => s.SessionId));
        Assert.Equal(2, r.Sessions[0].Requests);   // the 2026-01-31 request of C is outside the range
        Assert.Equal("2026-01-20T11:00:00.000Z", r.Sessions[0].LastAt);
        Assert.Equal("2026-01-10T00:00:00", r.From);
        Assert.Equal("2026-01-21T00:00:00", r.To);

        Assert.Equal(new[] { C }, store.UsageTop("2026-01-31", "2026-01-31", null, null).Sessions.Select(s => s.SessionId));
        Assert.Empty(store.UsageTop("2026-02-01", null, null, null).Sessions);
        Assert.True(store.UsageTop("2026-02-01", null, null, null).Recorded);
    }

    [Fact]
    public void Usage_top_accepts_full_timestamps_as_bounds()
    {
        using var fx = StoreWithUsage();

        var r = new ChatStore(fx.Paths).UsageTop("2026-01-20T10:30:00Z", null, "requests", null);

        Assert.Equal(new[] { C }, r.Sessions.Select(s => s.SessionId));
        Assert.Equal(2, r.Sessions[0].Requests);
    }

    [Fact]
    public void Usage_top_is_capped_by_limit_and_flagged()
    {
        using var fx = StoreWithUsage();

        var r = new ChatStore(fx.Paths).UsageTop(null, null, null, 2);

        Assert.Equal(new[] { A, B }, r.Sessions.Select(s => s.SessionId));
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Usage_top_limit_above_the_maximum_is_clamped_and_flagged()
    {
        using var fx = StoreWithUsage();

        var r = new ChatStore(fx.Paths).UsageTop(null, null, null, 1000);

        Assert.Equal(Caps.MaxLimit, r.Limit);
        Assert.Equal(3, r.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Usage_top_resolves_titles_like_sessions()
    {
        using var fx = StoreWithUsage();
        fx.WriteYaml(A, FixtureStore.SampleYaml(A, "Named chat"));
        const string Orphan = "44444444-4444-4444-4444-444444444444";
        fx.AddUsage(Orphan, 0, "m", 1, 1, nanoAiu: 1);
        fx.AddUsage("..\\evil", 0, "m", 1, 1, nanoAiu: 0);

        var r = new ChatStore(fx.Paths).UsageTop(null, null, null, null);

        Assert.Equal("Named chat", r.Sessions.Single(s => s.SessionId == A).Title);
        Assert.Equal("name", r.Sessions.Single(s => s.SessionId == A).TitleSource);
        Assert.Null(r.Sessions.Single(s => s.SessionId == Orphan).Title);
        Assert.Equal("none", r.Sessions.Single(s => s.SessionId == "..\\evil").TitleSource);
    }

    [Fact]
    public void Usage_top_rejects_an_unknown_by_value()
    {
        using var fx = StoreWithUsage();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).UsageTop(null, null, "cost", null));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    [Fact]
    public void Usage_top_rejects_a_bad_date()
    {
        using var fx = StoreWithUsage();
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).UsageTop("yesterday", null, null, null));
        Assert.Equal(ErrorKind.Usage, ex.Kind);
    }

    [Fact]
    public void Usage_top_without_the_table_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);

        var r = new ChatStore(fx.Paths).UsageTop(null, null, "tokens", null);

        Assert.False(r.Recorded);
        Assert.Empty(r.Sessions);
        Assert.Contains("no usage table", r.Note);
        Assert.Equal("tokens", r.By);
    }

    [Fact]
    public void Usage_top_with_a_table_missing_a_needed_column_is_a_normal_answer()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable(dropColumn: true);

        Assert.False(new ChatStore(fx.Paths).UsageTop(null, null, null, null).Recorded);
    }

    [Fact]
    public void Usage_top_reports_a_clear_error_when_a_sum_overflows_in_sqlite()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: long.MaxValue - 1);
        fx.AddUsage(A, 1, "m", 1, 1, nanoAiu: 5);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).UsageTop(null, null, null, null));

        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Usage_top_does_not_write_to_the_store()
    {
        using var fx = StoreWithUsage();
        var before = File.ReadAllBytes(fx.DbPath);
        new ChatStore(fx.Paths).UsageTop("2026-01-01", "2026-12-31", "tokens", 5);
        Assert.Equal(before, File.ReadAllBytes(fx.DbPath));
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
    public void Cli_usage_top_prints_snake_case_json()
    {
        using var fx = StoreWithUsage();

        var (exit, output) = Run(fx, "usage-top", "--by", "tokens", "--from", "2026-01-01", "--limit", "2");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("usage-top", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal("tokens", root.GetProperty("by").GetString());
        Assert.Equal("2026-01-01T00:00:00", root.GetProperty("from").GetString());
        Assert.False(root.TryGetProperty("to", out _));
        Assert.Equal(2, root.GetProperty("count").GetInt32());
        var first = root.GetProperty("sessions")[0];
        Assert.Equal(B, first.GetProperty("session_id").GetString());
        Assert.Equal(5000, first.GetProperty("input_tokens").GetInt64());
        Assert.Equal(500, first.GetProperty("output_tokens").GetInt64());
        Assert.Equal(1_000, first.GetProperty("nano_aiu").GetInt64());
        Assert.True(first.TryGetProperty("cache_read_tokens", out _));
        Assert.True(first.TryGetProperty("reasoning_tokens", out _));
        Assert.Equal("2026-01-10T10:00:00.000Z", first.GetProperty("last_at").GetString());
        Assert.Equal("chat b", first.GetProperty("title").GetString());
    }

    [Fact]
    public void Cli_usage_top_rejects_bad_arguments_with_usage_exit()
    {
        using var fx = StoreWithUsage();
        Assert.Equal(ExitCodes.Usage, Run(fx, "usage-top", "--by", "cost").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "usage-top", A).Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "usage-top", "--limit", "0").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "usage-top", "--to", "not-a-date").Exit);
    }

    [Fact]
    public void Cli_usage_top_without_the_table_is_a_normal_answer()
    {
        using var fx = new FixtureStore();

        var (exit, output) = Run(fx, "usage-top");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("recorded").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public void Cli_usage_top_text_mentions_coverage_and_the_cost_unit()
    {
        using var fx = StoreWithUsage();

        var (exit, output) = Run(fx, "usage-top", "--text");

        Assert.Equal(0, exit);
        Assert.Contains("usage-top: 3 chat(s) by aiu", output);
        Assert.Contains(A + "  2026-01-05T10:00:00.000Z  1 requests", output);
        Assert.Contains("nano_aiu 9000  chat a", output);
        Assert.Contains("coverage: session-store.db only", output);
    }

    [Fact]
    public void Text_output_cleans_control_characters_in_titles()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A, summary: "title\u001b]0;pwned\u0007\nFAKE: title");
        fx.AddUsage(A, 0, "m", 1, 1);

        var (_, output) = Run(fx, "usage-top", "--text");

        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFAKE:", output, StringComparison.Ordinal);
    }
}
