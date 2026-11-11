using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class UsageTurnsTests
{
    private const string A = "synthetic-session-a";
    private const string B = "synthetic-session-b";

    private static FixtureStore Store(bool withMultiplier = true, bool withId = true)
    {
        var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddUsageTable(withMultiplier: withMultiplier, withId: withId);
        return fx;
    }

    [Fact]
    public void Latest_twenty_work_after_100_turns_preserve_gaps_and_leave_default_usage_unchanged()
    {
        using var fx = Store();
        fx.Exec("""
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 164)
            INSERT INTO assistant_usage_events(session_id, turn_index, model, input_tokens, output_tokens,
                cache_read_tokens, cache_write_tokens, total_nano_aiu, request_multiplier, duration_ms, created_at)
            SELECT $id, i * 2, 'm', i + 1, 2, 0, 0, 3, 0.33, 1, '2026-01-01T00:00:00.000Z' FROM n
            """, ("$id", A));
        var store = new ChatStore(fx.Paths);

        var latest = store.UsageTurns(A, null);
        var original = store.Usage(A, true);

        Assert.Equal(20, latest.Limit);
        Assert.Equal(20, latest.Count);
        Assert.Equal(Enumerable.Range(145, 20).Select(i => (long)i * 2), latest.ByTurn.Select(x => x.TurnIndex));
        Assert.Equal(165, latest.Total!.Requests);
        Assert.Equal(Enumerable.Range(1, 165).Sum(), latest.Total.InputTokens);
        Assert.Equal(330, latest.Total.OutputTokens);
        Assert.Equal(495, latest.Total.NanoAiu);
        Assert.Equal(54.45, latest.Total.Credits);
        Assert.True(latest.Truncated);
        Assert.Contains("total covers all session requests", latest.Note);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (long)i * 2), original.ByTurn!.Select(x => x.TurnIndex));
        Assert.True(original.Truncated);
        Assert.Equal(original.Total, latest.Total);
        foreach (var limit in new[] { 100, int.MaxValue })
        {
            var capped = store.UsageTurns(A, limit);
            Assert.Equal(100, capped.Limit);
            Assert.Equal(100, capped.Count);
            Assert.Equal(Enumerable.Range(65, 100).Select(i => (long)i * 2), capped.ByTurn.Select(x => x.TurnIndex));
            Assert.True(capped.Truncated);
            Assert.Equal(latest.Total, capped.Total);
        }
        using var json = JsonDocument.Parse(JsonOut.Serialize(original));
        Assert.Equal(new[] { "peak_input_tokens", "totals", "turn_index" },
            json.RootElement.GetProperty("by_turn")[0].EnumerateObject().Select(x => x.Name).Order());
    }

    [Fact]
    public void Window_is_by_distinct_turn_index_not_insertion_order_timestamp_or_index_distance()
    {
        using var fx = Store();
        fx.AddUsage(A, 900, "m", 1, 1, createdAt: "2026-01-01T00:00:00.000Z");
        fx.AddUsage(A, 0, "m", 1, 1, createdAt: "2026-12-01T00:00:00.000Z");
        fx.AddUsage(A, 5, "m", 1, 1);
        fx.AddUsage(A, 900, "m", 2, 2, agentId: "subagent");
        fx.AddUsage(A, 80, "m", 1, 1);
        fx.AddUsage(B, 999, "m", 999, 999);

        var result = new ChatStore(fx.Paths).UsageTurns(A, 2);

        Assert.Equal(new long[] { 80, 900 }, result.ByTurn.Select(x => x.TurnIndex));
        Assert.Equal(2, result.ByTurn[1].Totals.Requests);
        Assert.Equal(5, result.Total!.Requests);
        Assert.Equal(6, result.Total.InputTokens);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Main_and_subagent_requests_share_the_same_turn_and_only_main_requests_set_peak()
    {
        using var fx = Store();
        fx.AddUsage(A, 2, "m", 100, 10, cacheRead: 50, cacheWrite: 20, reasoning: 3, nanoAiu: 5, durationMs: 200, multiplier: 0.33);
        fx.AddUsage(A, 2, "other", 900, 20, reasoning: 7, nanoAiu: 15, durationMs: 300, agentId: "sub", multiplier: 1);
        fx.AddUsage(A, 7, "other", 400, 30, nanoAiu: null, reasoning: null, agentId: "sub", multiplier: 7.5);

        var result = new ChatStore(fx.Paths).UsageTurns(A, null);

        Assert.False(result.Truncated);
        Assert.Null(result.Note);
        Assert.Equal(new long[] { 2, 7 }, result.ByTurn.Select(x => x.TurnIndex));
        Assert.Equal(new UsageTotals(2, 1000, 30, 50, 20, 10, 20, 1.33, 500), result.ByTurn[0].Totals);
        Assert.Equal(100, result.ByTurn[0].PeakInputTokens);
        Assert.Null(result.ByTurn[1].PeakInputTokens);
        Assert.Equal(0, result.ByTurn[1].Totals.NanoAiu);
        Assert.Equal(0, result.ByTurn[1].Totals.ReasoningTokens);
        Assert.Equal(new ChatStore(fx.Paths).Usage(A, false).Total, result.Total);
    }

    [Fact]
    public void Polling_replaces_streamed_rows_and_does_not_accumulate_previous_snapshots()
    {
        using var fx = Store();
        fx.AddUsage(A, 150, "m", 10, 1, nanoAiu: 5, multiplier: 1, createdAt: "2026-01-01T10:00:00.000Z");
        var store = new ChatStore(fx.Paths);
        var first = store.UsageTurns(A, null);
        fx.Exec("""
            UPDATE assistant_usage_events SET output_tokens = 20, total_nano_aiu = 8,
                request_multiplier = 3, created_at = '2026-01-01T10:01:00.000Z' WHERE session_id = $id
            """, ("$id", A));
        var replaced = store.UsageTurns(A, null);
        fx.AddUsage(A, 150, "m", 30, 2, agentId: "sub", multiplier: 0.33, createdAt: "2026-01-01T10:02:00.000Z");
        var appended = store.UsageTurns(A, null);

        Assert.Equal(150, Assert.Single(first.ByTurn).TurnIndex);
        Assert.Equal(150, Assert.Single(replaced.ByTurn).TurnIndex);
        Assert.Equal(150, Assert.Single(appended.ByTurn).TurnIndex);
        Assert.Equal(1, first.Total!.OutputTokens);
        Assert.Equal(1, replaced.Total!.Requests);
        Assert.Equal(20, replaced.Total.OutputTokens);
        Assert.Equal(3, replaced.Total.Credits);
        Assert.Equal("2026-01-01T10:01:00.000Z", replaced.ByTurn[0].FirstAt);
        Assert.Equal(replaced.ByTurn[0].FirstAt, replaced.ByTurn[0].LastAt);
        Assert.Equal(2, appended.Total!.Requests);
        Assert.Equal(22, appended.Total.OutputTokens);
        Assert.Equal(3.33, appended.Total.Credits);
        Assert.Equal("2026-01-01T10:02:00.000Z", appended.ByTurn[0].LastAt);
        Assert.Equal(appended.Total, store.UsageTurns(A, null).Total);
        fx.Exec("DELETE FROM assistant_usage_events WHERE session_id = $id", ("$id", A));
        fx.AddUsage(A, 150, "m", 50, 25, multiplier: 7.5, createdAt: "2026-01-01T10:03:00.000Z");
        var reinserted = store.UsageTurns(A, null);
        Assert.Equal(150, Assert.Single(reinserted.ByTurn).TurnIndex);
        Assert.Equal(1, reinserted.Total!.Requests);
        Assert.Equal(25, reinserted.Total.OutputTokens);
        Assert.Equal("2026-01-01T10:03:00.000Z", reinserted.ByTurn[0].FirstAt);
        fx.AddUsage(A, 170, "m", 1, 1);
        Assert.Equal(170, Assert.Single(store.UsageTurns(A, 1).ByTurn).TurnIndex);
    }

    [Fact]
    public void First_and_last_are_utc_usage_event_extrema_including_subagents_not_turn_message_times()
    {
        using var fx = Store();
        fx.AddTurn(A, 3, "synthetic message", "synthetic response", "2025-01-01T00:00:00.000Z");
        fx.AddUsage(A, 3, "m", 1, 1, createdAt: "2026-01-01T23:30:00.125-03:00", agentId: "sub");
        fx.AddUsage(A, 3, "m", 1, 1, createdAt: "2026-01-02T00:30:00.001+02:00");
        fx.AddUsage(A, 3, "m", 1, 1, createdAt: "2026-01-01T22:30:00.001Z");
        fx.AddUsage(A, 9, "m", 1, 1, createdAt: "2026-01-01T10:00:00Z", agentId: "sub");
        fx.AddUsage(A, 9, "m", 1, 1, createdAt: "2026-01-01T10:00:00+00:00", agentId: "sub");

        var result = new ChatStore(fx.Paths).UsageTurns(A, null);

        Assert.Equal("2026-01-01T22:30:00.001Z", result.ByTurn[0].FirstAt);
        Assert.Equal("2026-01-02T02:30:00.125Z", result.ByTurn[0].LastAt);
        Assert.True(result.ByTurn[0].TimestampsComplete);
        Assert.Equal("2026-01-01T10:00:00.000Z", result.ByTurn[1].FirstAt);
        Assert.Equal(result.ByTurn[1].FirstAt, result.ByTurn[1].LastAt);
        Assert.True(result.ByTurn[1].TimestampsComplete);
    }

    [Fact]
    public void Missing_or_invalid_timestamps_do_not_discard_usage_or_invent_times()
    {
        using var fx = Store();
        fx.Exec("ALTER TABLE assistant_usage_events DROP COLUMN created_at; ALTER TABLE assistant_usage_events ADD COLUMN created_at TEXT");
        fx.AddUsage(A, 0, "m", 1, 1, createdAt: "2026-01-01T00:00:00.000Z");
        fx.AddUsage(A, 0, "m", 2, 2, createdAt: "not-a-date");
        fx.AddUsage(A, 1, "m", 3, 3, createdAt: "");
        fx.Exec("INSERT INTO assistant_usage_events(session_id, turn_index, model, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, duration_ms) VALUES($id, 1, 'm', 4, 4, 0, 0, 100)", ("$id", A));

        var result = new ChatStore(fx.Paths).UsageTurns(A, null);

        Assert.Equal(4, result.Total!.Requests);
        Assert.Equal(10, result.Total.InputTokens);
        Assert.False(result.ByTurn[0].TimestampsComplete);
        Assert.Equal("2026-01-01T00:00:00.000Z", result.ByTurn[0].FirstAt);
        Assert.Equal(result.ByTurn[0].FirstAt, result.ByTurn[0].LastAt);
        Assert.False(result.ByTurn[1].TimestampsComplete);
        Assert.Null(result.ByTurn[1].FirstAt);
        Assert.Null(result.ByTurn[1].LastAt);
        using var json = JsonDocument.Parse(JsonOut.Serialize(result));
        Assert.False(json.RootElement.GetProperty("by_turn")[1].TryGetProperty("first_at", out _));
        Assert.False(json.RootElement.GetProperty("by_turn")[1].TryGetProperty("last_at", out _));
    }

    [Theory]
    [InlineData("2026-01-01T10:00:00", "2026-01-01T10:00:00.000Z")]
    [InlineData("2026-01-01T10:00:00.1", "2026-01-01T10:00:00.100Z")]
    [InlineData("2026-01-01T10:00:00.1234567Z", "2026-01-01T10:00:00.123Z")]
    [InlineData("2026-01-01T00:15:00+05:30", "2025-12-31T18:45:00.000Z")]
    public void Timestamp_precision_and_unspecified_offsets_have_explicit_utc_semantics(string timestamp, string expected)
    {
        using var fx = Store();
        fx.AddUsage(A, 10, "m", 1, 1, createdAt: timestamp);

        var turn = Assert.Single(new ChatStore(fx.Paths).UsageTurns(A, null).ByTurn);

        Assert.Equal(expected, turn.FirstAt);
        Assert.Equal(expected, turn.LastAt);
        Assert.True(turn.TimestampsComplete);
    }

    [Theory]
    [InlineData("now")]
    [InlineData("12:00:00")]
    [InlineData("2026-01-01")]
    [InlineData("2026-02-30T00:00:00Z")]
    [InlineData("2026-01-01T10:00:00+99:00")]
    public void Non_event_timestamp_values_do_not_fabricate_dates_or_use_the_poll_clock(string timestamp)
    {
        using var fx = Store();
        fx.AddUsage(A, 10, "m", 1, 1, createdAt: timestamp);

        var result = new ChatStore(fx.Paths).UsageTurns(A, null);
        var turn = Assert.Single(result.ByTurn);

        Assert.False(turn.TimestampsComplete);
        Assert.Null(turn.FirstAt);
        Assert.Null(turn.LastAt);
        Assert.Equal(1, turn.Totals.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Credits_stay_unknown_without_the_column_but_null_values_count_as_zero_when_it_exists(bool withMultiplier)
    {
        using var fx = Store(withMultiplier);
        fx.AddUsage(A, 0, "m", 1, 1);
        fx.AddUsage(A, 2, "m", 1, 1, multiplier: withMultiplier ? 0.125 : null);
        fx.AddUsage(A, 2, "m", 1, 1, multiplier: withMultiplier ? 0.125 : null, agentId: "sub");

        var result = new ChatStore(fx.Paths).UsageTurns(A, null);

        Assert.Equal(withMultiplier ? 0 : (double?)null, result.ByTurn[0].Totals.Credits);
        Assert.Equal(withMultiplier ? 0.25 : (double?)null, result.ByTurn[1].Totals.Credits);
        Assert.Equal(withMultiplier ? 0.25 : (double?)null, result.Total!.Credits);
        if (!withMultiplier) Assert.DoesNotContain("credits", JsonOut.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(3, 3, false)]
    [InlineData(100, 3, false)]
    [InlineData(101, 3, true)]
    public void Limits_are_bounded_and_only_truncate_the_window_not_the_total(int limit, int count, bool truncated)
    {
        using var fx = Store();
        for (var i = 0; i < 3; i++) fx.AddUsage(A, i * 10, "m", 1, 1);

        var result = new ChatStore(fx.Paths).UsageTurns(A, limit);

        Assert.Equal(Math.Min(limit, 100), result.Limit);
        Assert.Equal(count, result.Count);
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal(truncated, result.Note is not null);
        Assert.Equal(3, result.Total!.Requests);
        Assert.Equal(20, result.ByTurn[^1].TurnIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_usage_table_or_no_rows_has_a_clear_recorded_false_result(bool createTable)
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        if (createTable) fx.AddUsageTable();

        var result = new ChatStore(fx.Paths).UsageTurns(A, null);

        Assert.False(result.Recorded);
        Assert.NotNull(result.Note);
        Assert.Null(result.Total);
        Assert.Empty(result.ByTurn);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, 100)]
    [InlineData(0, 101)]
    [InlineData(0, int.MaxValue)]
    [InlineData(1, null)]
    [InlineData(1, 100)]
    [InlineData(1, 101)]
    [InlineData(1, int.MaxValue)]
    [InlineData(2, null)]
    [InlineData(2, 100)]
    [InlineData(2, 101)]
    [InlineData(2, int.MaxValue)]
    public void Unavailable_or_empty_usage_preserves_clamping_metadata(int state, int? limit)
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        if (state != 0) fx.AddUsageTable();
        if (state == 1) fx.Exec("ALTER TABLE assistant_usage_events DROP COLUMN created_at");
        var clamped = limit > 100;
        var reason = state == 2
            ? "No usage rows are recorded for this session."
            : "This store has no usage table (or it has an unexpected shape), so no token figures are available.";

        var result = new ChatStore(fx.Paths).UsageTurns(A, limit);

        Assert.False(result.Recorded);
        Assert.Equal(limit is null ? 20 : Math.Min(limit.Value, 100), result.Limit);
        Assert.Equal(clamped, result.Truncated);
        Assert.Equal(reason + (clamped ? " Requested limit was clamped to 100." : ""), result.Note);
        Assert.Null(result.Total);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.ByTurn);
        using var json = JsonDocument.Parse(JsonOut.Serialize(result));
        Assert.Equal(clamped, json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(result.Note, json.RootElement.GetProperty("note").GetString());
        Assert.False(json.RootElement.TryGetProperty("total", out _));
        Assert.DoesNotContain(A, JsonOut.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_usage_columns_is_explicit_and_models_and_row_ids_are_not_required()
    {
        using var fx = Store(withId: false);
        fx.AddUsage(A, 0, "m", 1, 1);
        fx.Exec("ALTER TABLE assistant_usage_events DROP COLUMN model");
        var store = new ChatStore(fx.Paths);

        Assert.True(store.UsageTurns(A, null).Recorded);
        fx.Exec("ALTER TABLE assistant_usage_events DROP COLUMN created_at");
        var missingTimestampColumn = store.UsageTurns(A, null);
        Assert.False(missingTimestampColumn.Recorded);
        Assert.NotNull(missingTimestampColumn.Note);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overflow_is_reported_even_in_requests_outside_the_latest_window(bool credits)
    {
        using var fx = Store();
        fx.AddUsage(A, 0, "m", 1, 1, nanoAiu: credits ? 1 : long.MaxValue, multiplier: credits ? double.MaxValue : 1);
        fx.AddUsage(A, 100, "m", 1, 1, nanoAiu: 1, multiplier: credits ? double.MaxValue : 1, agentId: "sub");

        var error = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).UsageTurns(A, 1));

        Assert.Equal(ErrorKind.StoreUnavailable, error.Kind);
        Assert.DoesNotContain(A, error.Message, StringComparison.Ordinal);
    }
}
