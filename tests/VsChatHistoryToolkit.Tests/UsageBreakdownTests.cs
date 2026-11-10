using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class UsageBreakdownTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";
    private const string D = "44444444-4444-4444-4444-444444444444";
    private const string E = "55555555-5555-5555-5555-555555555555";

    private static FixtureStore StoreWithUsage(bool withMultiplier = true)
    {
        var fx = new FixtureStore();
        fx.AddUsageTable(withMultiplier: withMultiplier);
        fx.AddSession(A, summary: "PRIVATE TITLE", repo: "org/alpha", cwd: @"C:\PRIVATE CWD");
        fx.AddSession(B, repo: "org/alpha");
        fx.AddSession(C, repo: "org/beta");
        fx.AddSession(D, repo: null);
        fx.AddSession(E, repo: "");
        fx.AddTurn(A, 0, "PRIVATE MESSAGE", "PRIVATE RESPONSE", "2025-12-01T10:00:00.000Z");
        fx.WriteYaml(A, FixtureStore.SampleYaml(A, "PRIVATE YAML TITLE"));
        double? Multiplier(double? value) => withMultiplier ? value : null;
        fx.AddUsage(A, 0, "Model-A", 100, 10, nanoAiu: 900, createdAt: "2026-01-01T23:59:59.999Z", multiplier: Multiplier(0.33));
        fx.AddUsage(A, 1, "Model-A", 200, 20, nanoAiu: 100, createdAt: "2026-01-02T00:00:00.000Z", multiplier: Multiplier(1), agentId: "agent-1");
        fx.AddUsage(A, 1, "Model-B", 300, 30, nanoAiu: 300, createdAt: "2026-01-02T12:00:00.000Z", multiplier: Multiplier(7.5));
        fx.AddUsage(B, 0, "Model-A", 400, 40, nanoAiu: 100, createdAt: "2026-01-02T13:00:00.000Z", multiplier: Multiplier(0.33));
        fx.AddUsage(C, 0, "Model-A", 500, 50, nanoAiu: 500, createdAt: "2026-01-03T00:00:00.000Z", multiplier: Multiplier(15));
        fx.AddUsage(D, 0, "Model-A", 600, 60, nanoAiu: null, createdAt: "2026-01-02T15:00:00.000Z");
        fx.AddUsage(E, 0, "Model-A", 700, 70, nanoAiu: 20, createdAt: "2026-01-02T16:00:00.000Z", multiplier: Multiplier(1));
        return fx;
    }

    [Fact]
    public void Default_groups_cover_multiple_days_models_and_repositories_and_reconcile_with_total()
    {
        using var fx = StoreWithUsage();

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, null, null);

        Assert.True(result.Recorded);
        Assert.False(result.Truncated);
        Assert.Null(result.Note);
        Assert.Equal(100, result.Limit);
        Assert.Equal(new[] { "day", "repo", "model" }, result.GroupBy);
        Assert.Equal(new UsageBreakdownTotals(7, 1920, 25.16, 2800, 280, 5), result.Total);
        Assert.Equal(new[]
        {
            new UsageBreakdownItem("2026-01-03", "org/beta", "Model-A", 1, 500, 15, 500, 50, 1),
            new UsageBreakdownItem("2026-01-02", "org/alpha", "Model-B", 1, 300, 7.5, 300, 30, 1),
            new UsageBreakdownItem("2026-01-02", "org/alpha", "Model-A", 2, 200, 1.33, 600, 60, 2),
            new UsageBreakdownItem("2026-01-02", "unknown", "Model-A", 2, 20, 1, 1300, 130, 2),
            new UsageBreakdownItem("2026-01-01", "org/alpha", "Model-A", 1, 900, 0.33, 100, 10, 1),
        }, result.Rows);
        AssertAdditiveTotals(result);
        // A spans two days and two models, so distinct chats cannot be added across its groups.
        Assert.Equal(7, result.Rows.Sum(x => x.Chats));
        Assert.Equal(5, result.Total!.Chats);
    }

    [Theory]
    [InlineData("day", 3)]
    [InlineData("repo", 3)]
    [InlineData("model", 2)]
    [InlineData("day,repo", 4)]
    [InlineData("day,model", 4)]
    [InlineData("repo,model", 4)]
    [InlineData("day,repo,model", 5)]
    public void Every_nonempty_group_combination_reconciles_and_only_serializes_selected_keys(string groupBy, int count)
    {
        using var fx = StoreWithUsage();

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, groupBy, null);

        Assert.Equal(count, result.Count);
        AssertAdditiveTotals(result);
        using var json = JsonDocument.Parse(JsonOut.Serialize(result));
        var expected = groupBy.Split(',').Concat(new[] { "requests", "nano_aiu", "credits", "input_tokens", "output_tokens", "chats" }).Order().ToArray();
        foreach (var row in json.RootElement.GetProperty("rows").EnumerateArray())
            Assert.Equal(expected, row.EnumerateObject().Select(x => x.Name).Order().ToArray());
        if (groupBy == "repo") Assert.Equal(result.Total!.Chats, result.Rows.Sum(x => x.Chats));
    }

    [Fact]
    public void Groups_accept_whitespace_and_case_and_are_normalized_to_canonical_key_order()
    {
        using var fx = StoreWithUsage();

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, " Model , DAY ", null);

        Assert.Equal(new[] { "day", "model" }, result.GroupBy);
        Assert.All(result.Rows, x => Assert.Null(x.Repo));
        AssertAdditiveTotals(result);
    }

    [Fact]
    public void Date_only_bounds_filter_requests_not_sessions_and_include_the_entire_to_day()
    {
        using var fx = StoreWithUsage();

        var result = new ChatStore(fx.Paths).UsageBreakdown("2026-01-02", "2026-01-02", null, null, null, null);

        Assert.Equal("2026-01-02T00:00:00.000Z", result.From);
        Assert.Equal("2026-01-03T00:00:00.000Z", result.To);
        Assert.Equal(new UsageBreakdownTotals(5, 520, 9.83, 2200, 220, 4), result.Total);
        Assert.All(result.Rows, x => Assert.Equal("2026-01-02", x.Day));
        AssertAdditiveTotals(result);
    }

    [Fact]
    public void Full_timestamp_bounds_are_half_open_and_keep_fractional_seconds()
    {
        using var fx = StoreWithUsage();
        var store = new ChatStore(fx.Paths);

        var beforeBoundary = store.UsageBreakdown("2026-01-01T23:59:59.999Z", "2026-01-02T00:00:00.000Z", null, null, null, null);
        var afterBoundary = store.UsageBreakdown("2026-01-02T00:00:00.000Z", "2026-01-02T00:00:00.001Z", null, null, null, null);

        Assert.Equal(new UsageBreakdownTotals(1, 900, 0.33, 100, 10, 1), beforeBoundary.Total);
        Assert.Equal(new UsageBreakdownTotals(1, 100, 1, 200, 20, 1), afterBoundary.Total);
        Assert.Equal("2026-01-01", Assert.Single(beforeBoundary.Rows).Day);
        Assert.Equal("2026-01-02", Assert.Single(afterBoundary.Rows).Day);
    }

    [Fact]
    public void Offset_timestamps_are_grouped_and_filtered_on_the_utc_request_date()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 1, 2, nanoAiu: 3, createdAt: "2026-01-02T00:30:00+02:00", multiplier: 0.33);

        var result = new ChatStore(fx.Paths).UsageBreakdown("2026-01-01T23:00:00+01:00", "2026-01-01", null, null, "day", null);
        var activity = new ChatStore(fx.Paths).Activity("2026-01-01T23:00:00+01:00", "2026-01-01", null);
        var top = new ChatStore(fx.Paths).UsageTop("2026-01-01T23:00:00+01:00", "2026-01-01", null, null);

        Assert.Equal("2026-01-01", Assert.Single(result.Rows).Day);
        Assert.Equal(1, result.Total!.Requests);
        Assert.Equal(activity.Days.Sum(x => x.Requests), result.Total.Requests);
        Assert.Equal(top.Sessions.Sum(x => x.Requests), result.Total.Requests);
        Assert.Equal(top.Sessions.Sum(x => x.NanoAiu), result.Total.NanoAiu);
        Assert.Equal(top.Sessions.Sum(x => x.Credits), result.Total.Credits);
        Assert.Empty(new ChatStore(fx.Paths).UsageBreakdown("2026-01-02", null, null, null, null, null).Rows);
        Assert.Empty(new ChatStore(fx.Paths).UsageTop("2026-01-02", null, null, null).Sessions);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("2026-01-02", "2026-01-02")]
    [InlineData("2026-01-01T23:59:59.999Z", "2026-01-02T13:00:00.000Z")]
    public void Total_matches_usage_top_and_activity_for_the_same_range(string? from, string? to)
    {
        using var fx = StoreWithUsage();
        var store = new ChatStore(fx.Paths);

        var result = store.UsageBreakdown(from, to, null, null, null, null);
        var top = store.UsageTop(from, to, null, 100);
        var activity = store.Activity(from, to, 100);

        Assert.False(top.Truncated);
        Assert.False(activity.Truncated);
        AssertAdditiveTotals(result);
        var total = result.Total!;
        Assert.Equal(top.Sessions.Sum(x => x.Requests), total.Requests);
        Assert.Equal(top.Sessions.Sum(x => x.NanoAiu), total.NanoAiu);
        Assert.Equal(top.Sessions.Sum(x => x.InputTokens), total.InputTokens);
        Assert.Equal(top.Sessions.Sum(x => x.OutputTokens), total.OutputTokens);
        Assert.Equal(top.Count, total.Chats);
        Assert.Equal(top.Sessions.Sum(x => x.Credits)!.Value, total.Credits!.Value, 2);
        Assert.Equal(activity.Days.Sum(x => x.Requests), total.Requests);
        Assert.Equal(activity.Days.Sum(x => x.NanoAiu), total.NanoAiu);
        Assert.Equal(activity.Days.Sum(x => x.Credits)!.Value, total.Credits.Value, 2);
    }

    [Fact]
    public void Combined_filters_apply_before_grouping_and_include_matching_subagent_requests()
    {
        using var fx = StoreWithUsage();

        var (exit, output) = Run(fx, "usage-breakdown", "--from", "2026-01-02", "--to", "2026-01-02",
            "--repo", " ALPHA ", "--model", " model-a ", "--group-by", "repo,model");

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        var row = Assert.Single(root.GetProperty("rows").EnumerateArray());
        Assert.Equal("org/alpha", row.GetProperty("repo").GetString());
        Assert.Equal("Model-A", row.GetProperty("model").GetString());
        Assert.False(row.TryGetProperty("day", out _));
        Assert.Equal(2, row.GetProperty("requests").GetInt64());
        Assert.Equal(200, row.GetProperty("nano_aiu").GetInt64());
        Assert.Equal(1.33, row.GetProperty("credits").GetDouble());
        Assert.Equal(600, row.GetProperty("input_tokens").GetInt64());
        Assert.Equal(60, row.GetProperty("output_tokens").GetInt64());
        Assert.Equal(2, row.GetProperty("chats").GetInt64());
        Assert.Equal(2, root.GetProperty("total").GetProperty("requests").GetInt64());
    }

    [Fact]
    public void Repository_and_model_contains_treat_sql_wildcards_and_quotes_literally()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A, repo: @"org/a%_\b'c");
        fx.AddSession(B, repo: "org/another");
        fx.AddUsage(A, 0, @"Model%_\'X", 1, 1);
        fx.AddUsage(B, 0, "ModelAnythingX", 1, 1);
        var store = new ChatStore(fx.Paths);

        var result = store.UsageBreakdown(null, null, @"%_\b'", @"%_\'x", "model", null);

        Assert.Equal(1, result.Total!.Requests);
        Assert.Equal(@"Model%_\'X", Assert.Single(result.Rows).Model);
        Assert.Null(result.Rows[0].Repo);
        Assert.Equal(store.Sessions(new ChatStore.SessionQuery(@"%_\b'", null, null, null, null, null)).Count, result.Total.Chats);
        Assert.Empty(store.UsageBreakdown(null, null, "' OR 1=1 --", null, null, null).Rows);
    }

    [Fact]
    public void Model_filter_is_case_insensitive_for_non_ascii_letters_too()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "Mod\u00e8le", 1, 1);

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, "MOD\u00c8LE", "model", null);

        Assert.Equal(1, result.Total!.Requests);
    }

    [Fact]
    public void Empty_and_null_repositories_and_requests_without_a_session_row_group_as_unknown()
    {
        using var fx = StoreWithUsage();
        fx.AddUsage("orphan", 0, "Model-A", 1, 1, nanoAiu: 2);
        var store = new ChatStore(fx.Paths);

        var result = store.UsageBreakdown(null, null, null, null, "repo", null);
        var unknown = result.Rows.Single(x => x.Repo == "unknown");

        Assert.Equal(3, unknown.Requests);
        Assert.Equal(3, unknown.Chats);
        Assert.Equal(22, unknown.NanoAiu);
        Assert.Equal(6, result.Total!.Chats);
        Assert.Empty(store.UsageBreakdown(null, null, "unknown", null, "repo", null).Rows);
    }

    [Fact]
    public void Whitespace_filters_do_not_restrict_the_result()
    {
        using var fx = StoreWithUsage();

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, "  ", "  ", null, null);

        Assert.Equal(7, result.Total!.Requests);
        AssertAdditiveTotals(result);
    }

    [Fact]
    public void Limit_cuts_rows_but_not_total_and_supplies_a_note()
    {
        using var fx = StoreWithUsage();
        var store = new ChatStore(fx.Paths);

        var complete = store.UsageBreakdown(null, null, null, null, null, null);
        var limited = store.UsageBreakdown(null, null, null, null, null, 2);
        var exact = store.UsageBreakdown(null, null, null, null, null, complete.Count);

        Assert.Equal(2, limited.Count);
        Assert.True(limited.Truncated);
        Assert.Contains("total covers all filtered requests", limited.Note);
        Assert.Equal(complete.Total, limited.Total);
        Assert.Equal(complete.Rows.Take(2), limited.Rows);
        Assert.True(limited.Rows.Sum(x => x.Requests) < limited.Total!.Requests);
        Assert.False(exact.Truncated);
        Assert.Null(exact.Note);
    }

    [Fact]
    public void Default_and_maximum_row_caps_are_100_and_500_and_never_limit_total()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.Exec("""
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 500)
            INSERT INTO assistant_usage_events(session_id, turn_index, model, input_tokens, output_tokens,
                cache_read_tokens, cache_write_tokens, total_nano_aiu, duration_ms, created_at)
            SELECT $id, 0, printf('model-%03d', i), 1, 1, 0, 0, 1, 1, '2026-01-01T00:00:00.000Z' FROM n
            """, ("$id", A));
        var store = new ChatStore(fx.Paths);

        var byDefault = store.UsageBreakdown(null, null, null, null, "model", null);
        var maximum = store.UsageBreakdown(null, null, null, null, "model", 500);
        var clamped = store.UsageBreakdown(null, null, null, null, "model", 1000);

        Assert.Equal(100, byDefault.Limit);
        Assert.Equal(100, byDefault.Count);
        Assert.Equal(500, maximum.Limit);
        Assert.Equal(500, maximum.Count);
        Assert.Equal(500, clamped.Limit);
        Assert.Equal(500, clamped.Count);
        Assert.Equal("model-000", maximum.Rows[0].Model);
        Assert.Equal("model-499", maximum.Rows[^1].Model);
        foreach (var result in new[] { byDefault, maximum, clamped })
        {
            Assert.True(result.Truncated);
            Assert.NotNull(result.Note);
            Assert.Equal(501, result.Total!.Requests);
            Assert.Equal(501, result.Total.NanoAiu);
        }
    }

    [Fact]
    public void Clamped_limit_is_flagged_even_when_all_rows_fit()
    {
        using var fx = StoreWithUsage();

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, null, 501);

        Assert.Equal(500, result.Limit);
        Assert.Equal(5, result.Count);
        Assert.True(result.Truncated);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Ordering_is_day_descending_then_cost_descending_then_repository_and_model()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A, repo: "z/repo");
        fx.AddSession(B, repo: "a/repo");
        fx.AddUsage(A, 0, "z", 1, 1, nanoAiu: 5);
        fx.AddUsage(B, 0, "z", 1, 1, nanoAiu: 5);
        fx.AddUsage(B, 0, "a", 1, 1, nanoAiu: 5);
        fx.AddUsage(A, 1, "new", 1, 1, nanoAiu: 1, createdAt: "2026-01-06T00:00:00.000Z");
        var store = new ChatStore(fx.Paths);

        var all = store.UsageBreakdown(null, null, null, null, null, null);
        var models = store.UsageBreakdown(null, null, null, null, "model", null);

        Assert.Equal(new[] { "new", "a", "z", "z" }, all.Rows.Select(x => x.Model));
        Assert.Equal(new[] { "z/repo", "a/repo", "a/repo", "z/repo" }, all.Rows.Select(x => x.Repo));
        Assert.Equal(new[] { "z", "a", "new" }, models.Rows.Select(x => x.Model));
    }

    [Fact]
    public void Missing_request_multiplier_omits_credits_in_rows_total_json_and_text_never_fabricating_zero()
    {
        using var fx = StoreWithUsage(withMultiplier: false);

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, null, null);

        Assert.True(result.Recorded);
        Assert.Null(result.Total!.Credits);
        Assert.All(result.Rows, x => Assert.Null(x.Credits));
        Assert.DoesNotContain("credits", JsonOut.Serialize(result));
        Assert.DoesNotContain("credits", Run(fx, "usage-breakdown", "--text").Output);
        AssertAdditiveTotals(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_matches_is_recorded_with_zero_totals_and_an_empty_rows_array(bool withMultiplier)
    {
        using var fx = StoreWithUsage(withMultiplier);

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, "no-such-repo", null, null, null);

        Assert.True(result.Recorded);
        Assert.False(result.Truncated);
        Assert.Null(result.Note);
        Assert.Equal(new UsageBreakdownTotals(0, 0, withMultiplier ? 0 : null, 0, 0, 0), result.Total);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Only_columns_needed_by_the_breakdown_are_required()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.Exec("""
            CREATE TABLE assistant_usage_events(session_id TEXT, created_at TEXT, model TEXT,
                input_tokens INTEGER, output_tokens INTEGER, total_nano_aiu INTEGER);
            INSERT INTO assistant_usage_events VALUES($id, '2026-01-01T00:00:00.000Z', 'm', 10, 2, 3)
            """, ("$id", A));

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, null, null);

        Assert.True(result.Recorded);
        Assert.Equal(new UsageBreakdownTotals(1, 3, null, 10, 2, 1), result.Total);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Credits_use_the_existing_usage_rounding_and_null_multiplier_rules()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 1, 1, multiplier: 0.125);
        fx.AddUsage(A, 0, "m", 1, 1);
        var store = new ChatStore(fx.Paths);

        var result = store.UsageBreakdown(null, null, null, null, null, null);

        Assert.Equal(0.13, result.Total!.Credits);
        Assert.Equal(result.Total.Credits, Assert.Single(result.Rows).Credits);
        Assert.Equal(store.UsageTop(null, null, null, null).Sessions[0].Credits, result.Total.Credits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_unusable_usage_table_is_explicitly_reported(bool unusable)
    {
        using var fx = new FixtureStore();
        if (unusable) fx.AddUsageTable(dropColumn: true);

        var result = new ChatStore(fx.Paths).UsageBreakdown(null, null, null, null, null, null);

        Assert.False(result.Recorded);
        Assert.Null(result.Total);
        Assert.Empty(result.Rows);
        Assert.Contains("no usage table", result.Note);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overflow_in_all_filtered_requests_is_reported_even_when_its_group_is_not_returned(bool credits)
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A);
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: credits ? 1 : long.MaxValue, multiplier: credits ? double.MaxValue : 1,
            createdAt: "2026-01-01T00:00:00.000Z");
        fx.AddUsage(A, 1, "m", 1, 1, nanoAiu: 1, multiplier: credits ? double.MaxValue : 1,
            createdAt: "2026-01-02T00:00:00.000Z");

        var (exit, output) = Run(fx, "usage-breakdown", "--limit", "1");

        Assert.Equal(ExitCodes.StoreUnavailable, exit);
        using var json = JsonDocument.Parse(output);
        Assert.Equal("store_unavailable", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("too large", output);
    }

    [Fact]
    public void Command_is_read_only_and_does_not_expose_session_identifiers_titles_messages_or_cwd()
    {
        using var fx = StoreWithUsage();
        var before = File.ReadAllBytes(fx.DbPath);

        var json = Run(fx, "usage-breakdown");
        var text = Run(fx, "usage-breakdown", "--text");
        var hiddenRepo = Run(fx, "usage-breakdown", "--repo", "alpha", "--group-by", "day,model");

        Assert.Equal(0, json.Exit);
        Assert.Equal(0, text.Exit);
        Assert.Equal(before, File.ReadAllBytes(fx.DbPath));
        foreach (var output in new[] { json.Output, text.Output, hiddenRepo.Output })
        {
            Assert.DoesNotContain("PRIVATE", output);
            Assert.DoesNotContain(A, output);
            Assert.DoesNotContain("session_id", output);
            Assert.DoesNotContain("title", output);
            Assert.DoesNotContain("cwd", output);
        }
        Assert.DoesNotContain("alpha", hiddenRepo.Output);
        using var doc = JsonDocument.Parse(json.Output);
        var root = doc.RootElement;
        Assert.Equal("usage-breakdown", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(5, root.GetProperty("count").GetInt32());
        Assert.Equal(new[] { "chats", "credits", "input_tokens", "nano_aiu", "output_tokens", "requests" },
            root.GetProperty("total").EnumerateObject().Select(x => x.Name).Order());
        Assert.Contains("total: 7 requests, nano_aiu 1920, credits 25.16, in 2800, out 280, 5 chats", text.Output);
        Assert.Contains("coverage: session-store.db only", text.Output);
        Assert.Contains("usage-breakdown", CliRunner.Usage);
    }

    [Fact]
    public void Text_output_cleans_control_characters_in_group_keys()
    {
        using var fx = new FixtureStore();
        fx.AddUsageTable();
        fx.AddSession(A, repo: "org/repo\u001b]0;title\u0007\nFAKE");
        fx.AddUsage(A, 0, "model\nFAKE\u001b", 1, 1);

        var (exit, output) = Run(fx, "usage-breakdown", "--text");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0007", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFAKE", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--group-by", "")]
    [InlineData("--group-by", " ")]
    [InlineData("--group-by", "day,")]
    [InlineData("--group-by", ",day")]
    [InlineData("--group-by", "day,,repo")]
    [InlineData("--group-by", "day,day")]
    [InlineData("--group-by", "repo,REPO")]
    [InlineData("--group-by", "cwd")]
    [InlineData("--group-by", "day); DROP TABLE sessions; --")]
    [InlineData("--from", "not-a-date")]
    [InlineData("--to", "2026-02-30")]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "-1")]
    [InlineData("--limit", "not-a-number")]
    [InlineData("extra", null)]
    public void Bad_group_date_limit_or_positional_arguments_are_usage_errors(string argument, string? value)
    {
        using var fx = new FixtureStore();
        var args = value is null ? new[] { "usage-breakdown", argument } : new[] { "usage-breakdown", argument, value };

        var (exit, output) = Run(fx, args);

        Assert.Equal(ExitCodes.Usage, exit);
        using var json = JsonDocument.Parse(output);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("usage", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertAdditiveTotals(UsageBreakdownResult result)
    {
        Assert.False(result.Truncated);
        var total = Assert.IsType<UsageBreakdownTotals>(result.Total);
        Assert.Equal(total.Requests, result.Rows.Sum(x => x.Requests));
        Assert.Equal(total.NanoAiu, result.Rows.Sum(x => x.NanoAiu));
        Assert.Equal(total.InputTokens, result.Rows.Sum(x => x.InputTokens));
        Assert.Equal(total.OutputTokens, result.Rows.Sum(x => x.OutputTokens));
        if (total.Credits is { } credits)
            Assert.Equal(credits, result.Rows.Sum(x => x.Credits)!.Value, 2);
        else
            Assert.All(result.Rows, x => Assert.Null(x.Credits));
    }

    private static (int Exit, string Output) Run(FixtureStore fx, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), stdout, stderr);
        return (exit, stdout.ToString());
    }
}
