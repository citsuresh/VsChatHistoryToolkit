using System.Text;
using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class RollupTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";
    private const string D = "44444444-4444-4444-4444-444444444444";

    private static string Compaction(string trigger, long pre, long removed) =>
        "{\"type\":\"session.compaction_complete\",\"data\":{\"trigger\":\"" + trigger + "\",\"preCompactionTokens\":" + pre + ",\"tokensRemoved\":" + removed + "}}";

    private static string Shutdown(long premium, long nano, long added, long removed) =>
        "{\"type\":\"session.shutdown\",\"data\":{\"totalPremiumRequests\":" + premium + ",\"totalNanoAiu\":" + nano
        + ",\"codeChanges\":{\"linesAdded\":" + added + ",\"linesRemoved\":" + removed + ",\"filesModified\":[\"SECRET_PATH\"]}}}";

    private const string Abort = "{\"type\":\"abort\"}";

    /// <summary>A chat whose session end is the given day (no turns, so created_at is the end).</summary>
    private static void AddChat(FixtureStore fx, string id, int day) =>
        fx.AddSession(id, created: $"2026-01-{day:00}T10:00:00.000Z", updated: $"2026-01-{day:00}T10:00:00.000Z");

    [Fact]
    public void Rollup_of_an_empty_store_gives_zero_counts()
    {
        using var fx = new FixtureStore();

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(0, r.ChatsConsidered);
        Assert.Equal(0, r.ChatsScanned);
        Assert.Equal(0, r.ChatsSkipped);
        Assert.Equal(0, r.ChatsWithoutLog);
        Assert.Equal(0, r.BytesScanned);
        Assert.Empty(r.EventCounts);
        Assert.Empty(r.Tools);
        Assert.Equal(0, r.Compactions.Count);
        Assert.Null(r.Compactions.MedianPreCompactionTokens);
        Assert.Null(r.Compactions.MaxPreCompactionTokens);
        Assert.Null(r.Compactions.MedianTokensRemoved);
        Assert.Equal(new RollupShutdownTotals(0, 0, 0, 0), r.ShutdownTotals);
        Assert.Empty(r.TopChatsByCompactions);
        Assert.Equal(Caps.DefaultLimit, r.Limit);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Rollup_sums_counts_and_takes_medians_across_chats()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        AddChat(fx, C, 8);
        AddChat(fx, D, 7);
        fx.WriteEvents(A, new[]
        {
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"Edit\",\"args\":{\"path\":\"SECRET_PATH\"}}}",
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"Edit\"}}",
            "{\"type\":\"skill.invoked\",\"data\":{\"name\":\"review\"}}",
            "{\"type\":\"permission.requested\",\"data\":{\"permissionRequest\":{\"kind\":\"write\"}}}",
            "{\"type\":\"user.message\",\"data\":{\"message\":\"SECRET_MESSAGE\"}}",
            Abort,
            "{\"type\":\"subagent.started\"}",
            Compaction("context", 100, 40),
            Compaction("manual", 300, 61),
            Shutdown(2, 500, 4, 1),
        });
        fx.WriteEvents(B, new[]
        {
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"Edit\"}}",
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"Read\"}}",
            "{\"type\":\"skill.invoked\",\"data\":{\"name\":\"review\"}}",
            Compaction("context", 200, 50),
            Shutdown(3, 100, 1, 2),
        });
        // C has no events.jsonl; D has a complete log without a shutdown.
        fx.WriteEvents(D, new[] { Abort, Compaction("context", 400, 70) });

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(4, r.ChatsConsidered);
        Assert.Equal(3, r.ChatsScanned);
        Assert.Equal(0, r.ChatsSkipped);
        Assert.Equal(1, r.ChatsWithoutLog);
        Assert.Equal(0, r.ChatsWithIncompleteScan);
        Assert.Equal(1, r.ChatsWithoutShutdown);
        Assert.Equal(new FileInfo(fx.EventsPath(A)).Length + new FileInfo(fx.EventsPath(B)).Length + new FileInfo(fx.EventsPath(D)).Length, r.BytesScanned);
        Assert.Equal(4, r.EventCounts.Single(x => x.Type == "tool.execution_start").Count);
        Assert.Equal(4, r.EventCounts.Single(x => x.Type == "session.compaction_complete").Count);
        Assert.Equal(new[] { ("Edit", 3L), ("Read", 1L) }, r.Tools.Select(x => (x.Name, x.Count)));
        Assert.Equal(("review", 2L), (Assert.Single(r.Skills).Name, r.Skills[0].Count));
        Assert.Equal("write", Assert.Single(r.PermissionKinds).Name);
        Assert.Equal(2, r.Aborts);
        Assert.Equal(1, r.SubagentsStarted);

        Assert.Equal(4, r.Compactions.Count);
        Assert.Equal(new[] { ("context", 3L), ("manual", 1L) }, r.Compactions.ByTrigger.Select(x => (x.Name, x.Count)));
        Assert.Equal(250, r.Compactions.MedianPreCompactionTokens);   // 100 200 300 400
        Assert.Equal(400, r.Compactions.MaxPreCompactionTokens);
        Assert.Equal(55, r.Compactions.MedianTokensRemoved);          // 40 50 61 70: 55.5 rounded down
        Assert.Equal(new RollupShutdownTotals(5, 600, 5, 3), r.ShutdownTotals);
        Assert.Equal(new[] { (A, 2L), (B, 1L), (D, 1L) }, r.TopChatsByCompactions.Select(x => (x.SessionId, x.Count)));
        Assert.False(r.Truncated);

        var json = JsonOut.Serialize(r);
        Assert.DoesNotContain("SECRET_PATH", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_MESSAGE", json, StringComparison.Ordinal);
        Assert.DoesNotContain(fx.Root.Replace("\\", "\\\\"), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollup_median_of_an_odd_count_is_the_middle_value()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        fx.WriteEvents(A, new[] { Compaction("context", 900, 10), Compaction("context", 100, 30), Compaction("context", 500, 20) });

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(500, r.Compactions.MedianPreCompactionTokens);
        Assert.Equal(900, r.Compactions.MaxPreCompactionTokens);
        Assert.Equal(20, r.Compactions.MedianTokensRemoved);
    }

    [Fact]
    public void Rollup_top_chats_by_compactions_keeps_five_ordered_by_count_then_id()
    {
        using var fx = new FixtureStore();
        var ids = Enumerable.Range(1, 7).Select(i => $"{i}0000000-0000-0000-0000-000000000000").ToArray();
        var counts = new[] { 1, 3, 3, 0, 2, 5, 1 };
        for (var i = 0; i < ids.Length; i++)
        {
            AddChat(fx, ids[i], 10 + i);
            fx.WriteEvents(ids[i], Enumerable.Repeat(Compaction("context", 10, 1), counts[i]).Append(Shutdown(0, 0, 0, 0)));
        }

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(new[] { (ids[5], 5L), (ids[1], 3L), (ids[2], 3L), (ids[4], 2L), (ids[0], 1L) },
            r.TopChatsByCompactions.Select(x => (x.SessionId, x.Count)));
        Assert.Equal(15, r.Compactions.Count);
    }

    [Fact]
    public void Rollup_counts_an_incomplete_scan_separately_from_a_missing_shutdown()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        var longLine = "{\"type\":\"abort\",\"data\":{\"padding\":\"" + new string('x', Caps.EventsMaxLineBytes) + "\"}}";
        fx.WriteEventsRaw(A, longLine + "\n" + Abort);

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(1, r.ChatsScanned);
        Assert.Equal(1, r.ChatsWithIncompleteScan);
        Assert.Equal(0, r.ChatsWithoutShutdown);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_skips_an_unreadable_log_without_failing_or_naming_the_path()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        fx.WriteEvents(A, new[] { Abort });
        fx.WriteEvents(B, new[] { Abort, Shutdown(1, 1, 1, 1) });
        using var locked = new FileStream(fx.EventsPath(A), FileMode.Open, FileAccess.Read, FileShare.None);

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(2, r.ChatsConsidered);
        Assert.Equal(1, r.ChatsScanned);
        Assert.Equal(1, r.ChatsSkipped);
        Assert.Equal(1, r.Aborts);
        Assert.Equal(1, r.ShutdownTotals.PremiumRequests);
        Assert.True(r.Truncated);
        var (exit, output) = Run(fx, "rollup", "--text");
        Assert.Equal(0, exit);
        Assert.DoesNotContain(fx.Root, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("skipped 1", output);
    }

    [Fact]
    public void Rollup_stops_starting_scans_when_the_time_budget_is_spent()
    {
        using var fx = new FixtureStore();
        foreach (var (id, day) in new[] { (A, 10), (B, 9), (C, 8), (D, 7) })
        {
            AddChat(fx, id, day);
            fx.WriteEvents(id, new[] { Abort });
        }
        // The clock is read once at the start and once before each scan; every read moves it 25 seconds on.
        var now = FixtureStore.Now;
        var store = new ChatStore(fx.Paths, () => now = now.AddSeconds(25));

        var r = store.Rollup(null, null, null);

        // Before A: 25 s, before B: 50 s, before C: 75 s (over 60), so C and D are skipped.
        Assert.Equal(4, r.ChatsConsidered);
        Assert.Equal(2, r.ChatsScanned);
        Assert.Equal(2, r.ChatsSkipped);
        Assert.Equal(2, r.Aborts);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_with_a_fixed_clock_scans_everything()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        fx.WriteEvents(A, new[] { Abort, Shutdown(0, 0, 0, 0) });
        fx.WriteEvents(B, new[] { Abort, Shutdown(0, 0, 0, 0) });

        var r = new ChatStore(fx.Paths, () => FixtureStore.Now).Rollup(null, null, null);

        Assert.Equal(2, r.ChatsScanned);
        Assert.Equal(0, r.ChatsSkipped);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Rollup_stops_starting_scans_when_the_byte_budget_would_be_exceeded()
    {
        using var fx = new FixtureStore();
        foreach (var (id, day) in new[] { (A, 10), (B, 9), (C, 8) })
        {
            AddChat(fx, id, day);
            fx.WriteEvents(id, new[] { Abort, Shutdown(0, 0, 0, 0) });
        }
        var size = new FileInfo(fx.EventsPath(A)).Length;
        var store = new ChatStore(fx.Paths) { RollupMaxBytes = size * 3 - 1 };

        var r = store.Rollup(null, null, null);

        Assert.Equal(2, r.ChatsScanned);
        Assert.Equal(1, r.ChatsSkipped);
        Assert.Equal(size * 2, r.BytesScanned);
        Assert.True(r.Truncated);

        var exact = new ChatStore(fx.Paths) { RollupMaxBytes = size * 3 }.Rollup(null, null, null);
        Assert.Equal(3, exact.ChatsScanned);
        Assert.False(exact.Truncated);
    }

    [Fact]
    public void Rollup_byte_budget_uses_the_file_size_before_scanning()
    {
        using var fx = new FixtureStore();
        const string E = "55555555-5555-5555-5555-555555555555";
        foreach (var (id, day) in new[] { (A, 10), (B, 9), (C, 8), (D, 7), (E, 6) })
        {
            AddChat(fx, id, day);
            fx.WriteEvents(id, new[] { Abort, Shutdown(0, 0, 0, 0) });
        }
        // Each file reports 300 MiB, counted at the 128 MiB per-file cap: four fill the 512 MiB budget exactly.
        var store = new ChatStore(fx.Paths) { FileLength = _ => 300L * 1024 * 1024 };

        var r = store.Rollup(null, null, null);

        Assert.Equal(4, r.ChatsScanned);
        Assert.Equal(1, r.ChatsSkipped);
        Assert.Equal(4L * Caps.EventsMaxBytes, r.BytesScanned);
        Assert.Equal(Caps.RollupMaxBytes, r.BytesScanned);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_chooses_chats_like_sessions_newest_first_with_the_date_filter_and_limit()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 12);
        AddChat(fx, C, 8);
        fx.AddTurn(C, 0, "hello", "hi", "2026-01-11T09:00:00.000Z");   // C's end is its last turn, not created_at
        foreach (var (id, n) in new[] { (A, 1), (B, 2), (C, 3) })
            fx.WriteEvents(id, Enumerable.Repeat(Compaction("context", 10, 1), n).Append(Shutdown(0, 0, 0, 0)));

        var limited = new ChatStore(fx.Paths).Rollup(null, null, 2);
        Assert.Equal(2, limited.ChatsConsidered);
        Assert.Equal(new[] { B, C }, limited.TopChatsByCompactions.Select(x => x.SessionId).OrderBy(x => x));
        Assert.True(limited.Truncated);

        var filtered = new ChatStore(fx.Paths).Rollup("2026-01-10", "2026-01-11", null);
        Assert.Equal(2, filtered.ChatsConsidered);
        Assert.Equal(new[] { C, A }, filtered.TopChatsByCompactions.Select(x => x.SessionId));
        Assert.Equal("2026-01-10T00:00:00.000Z", filtered.From);
        Assert.Equal("2026-01-12T00:00:00.000Z", filtered.To);
        Assert.False(filtered.Truncated);
    }

    [Fact]
    public void Rollup_clamps_the_limit_and_rejects_bad_options()
    {
        using var fx = new FixtureStore();
        var r = new ChatStore(fx.Paths).Rollup(null, null, 500);
        Assert.Equal(Caps.MaxLimit, r.Limit);
        Assert.True(r.Truncated);

        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Rollup(null, null, 0)).Kind);
        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Rollup("yesterday", null, null)).Kind);
        Assert.Equal(ExitCodes.Usage, Run(fx, "rollup", "extra").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "rollup", "--to", "nope").Exit);
    }

    [Fact]
    public void Rollup_keeps_the_top_25_names_across_chats_and_sets_truncated()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        fx.WriteEvents(A, Enumerable.Range(0, 20).Select(i => "{\"type\":\"custom." + i.ToString("00") + "\"}").Append(Shutdown(0, 0, 0, 0)));
        fx.WriteEvents(B, Enumerable.Range(10, 15).Select(i => "{\"type\":\"custom." + i.ToString("00") + "\"}"));

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        // 26 distinct types (custom.00..24 and session.shutdown); custom.10..19 appear twice and lead.
        Assert.Equal(Caps.RollupTop, r.EventCounts.Count);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => ("custom." + i.ToString("00"), 2L)), r.EventCounts.Take(10).Select(x => (x.Type, x.Count)));
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_saturates_shutdown_totals_on_overflow()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        fx.WriteEvents(A, new[] { Shutdown(1, long.MaxValue - 1, 0, 0) });
        fx.WriteEvents(B, new[] { Shutdown(1, 5, 0, 0) });

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(long.MaxValue, r.ShutdownTotals.NanoAiu);
        Assert.Equal(2, r.ShutdownTotals.PremiumRequests);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_stops_when_exactly_60_seconds_have_passed()
    {
        using var fx = new FixtureStore();
        foreach (var (id, day) in new[] { (A, 10), (B, 9), (C, 8) })
        {
            AddChat(fx, id, day);
            fx.WriteEvents(id, new[] { Abort, Shutdown(0, 0, 0, 0) });
        }
        // Every clock read moves 20 seconds on: elapsed is 20 s before A, 40 s before B and exactly 60 s before C.
        var now = FixtureStore.Now;
        var r = new ChatStore(fx.Paths, () => now = now.AddSeconds(20)).Rollup(null, null, null);

        Assert.Equal(2, r.ChatsScanned);
        Assert.Equal(1, r.ChatsSkipped);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_just_under_60_seconds_still_starts_the_scan()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        fx.WriteEvents(A, new[] { Abort, Shutdown(0, 0, 0, 0) });
        fx.WriteEvents(B, new[] { Abort, Shutdown(0, 0, 0, 0) });
        // Elapsed is 29.999 s before A and 59.998 s before B.
        var now = FixtureStore.Now;
        var r = new ChatStore(fx.Paths, () => now = now.AddMilliseconds(29_999)).Rollup(null, null, null);

        Assert.Equal(2, r.ChatsScanned);
        Assert.Equal(0, r.ChatsSkipped);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Rollup_counts_a_folder_named_events_jsonl_as_skipped()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        Directory.CreateDirectory(fx.EventsPath(A));
        fx.WriteEvents(B, new[] { Abort, Shutdown(0, 0, 0, 0) });

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(2, r.ChatsConsidered);
        Assert.Equal(1, r.ChatsScanned);
        Assert.Equal(1, r.ChatsSkipped);
        Assert.Equal(0, r.ChatsWithoutLog);
        Assert.True(r.Truncated);
        var (exit, output) = Run(fx, "rollup", "--text");
        Assert.Equal(0, exit);
        Assert.DoesNotContain(fx.Root, output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollup_counts_a_stored_id_that_fails_validation_as_skipped()
    {
        using var fx = new FixtureStore();
        AddChat(fx, "bad.id", 11);
        AddChat(fx, A, 10);
        fx.WriteEvents(A, new[] { Abort, Shutdown(0, 0, 0, 0) });

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(2, r.ChatsConsidered);
        Assert.Equal(1, r.ChatsScanned);
        Assert.Equal(1, r.ChatsSkipped);
        Assert.Equal(0, r.ChatsWithoutLog);
        Assert.Equal(1, r.Aborts);
        Assert.True(r.Truncated);
        Assert.Equal(0, Run(fx, "rollup").Exit);
    }

    [Fact]
    public void Rollup_triggers_and_medians_cover_the_first_25_compactions_while_the_count_is_complete()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        // 25 "context" compactions with pre 10..250 and removed 1..25, then 5 "manual" ones the events cap drops.
        var lines = Enumerable.Range(1, Caps.EventsCompactions).Select(i => Compaction("context", i * 10, i))
            .Concat(Enumerable.Repeat(Compaction("manual", 10_000, 9_000), 5))
            .Append(Shutdown(0, 0, 0, 0));
        fx.WriteEvents(A, lines);

        var r = new ChatStore(fx.Paths).Rollup(null, null, null);

        Assert.Equal(Caps.EventsCompactions + 5, r.Compactions.Count);
        Assert.Equal(new[] { ("context", (long)Caps.EventsCompactions) }, r.Compactions.ByTrigger.Select(x => (x.Name, x.Count)));
        Assert.Equal(130, r.Compactions.MedianPreCompactionTokens);
        Assert.Equal(250, r.Compactions.MaxPreCompactionTokens);
        Assert.Equal(13, r.Compactions.MedianTokensRemoved);
        Assert.Equal((A, (long)Caps.EventsCompactions + 5), (r.TopChatsByCompactions[0].SessionId, r.TopChatsByCompactions[0].Count));
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Rollup_saturates_merged_name_counts_and_the_compaction_count_on_overflow()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        AddChat(fx, B, 9);
        fx.WriteEvents(A, new[] { Abort });
        fx.WriteEvents(B, new[] { Abort });
        static EventsResult Huge(string id) => new(id, true, null, new EventScan(true, 10, 1),
            new[] { new EventCountItem("abort", long.MaxValue - 1) },
            new[] { new EventNameCount("Edit", long.MaxValue - 1) },
            new[] { new EventNameCount("write", long.MaxValue - 1) },
            new[] { new EventNameCount("review", long.MaxValue - 1) },
            0, 0, long.MaxValue - 1, Array.Empty<EventCompaction>(),
            new[] { new EventShutdown(null, 0, 0, 0, 0, null, null, null, null, null, null, null) });

        var one = new ChatStore(fx.Paths) { ScanEvents = (id, _) => Huge(id) }.Rollup("2026-01-10", null, null);
        Assert.Equal(1, one.ChatsConsidered);
        Assert.Equal(long.MaxValue - 1, one.Compactions.Count);
        Assert.False(one.Truncated);

        var r = new ChatStore(fx.Paths) { ScanEvents = (id, _) => Huge(id) }.Rollup(null, null, null);

        Assert.Equal(long.MaxValue, Assert.Single(r.EventCounts).Count);
        Assert.Equal(long.MaxValue, Assert.Single(r.Tools).Count);
        Assert.Equal(long.MaxValue, Assert.Single(r.PermissionKinds).Count);
        Assert.Equal(long.MaxValue, Assert.Single(r.Skills).Count);
        Assert.Equal(long.MaxValue, r.Compactions.Count);
        Assert.Equal(new[] { A, B }, r.TopChatsByCompactions.Select(x => x.SessionId));
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Cli_rollup_json_has_envelope_and_snake_case_fields()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        fx.WriteEvents(A, new[] { Compaction("context", 100, 40), Shutdown(2, 500, 4, 1) });

        var (exit, output) = Run(fx, "rollup");

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("rollup", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, root.GetProperty("chats_scanned").GetInt64());
        Assert.Equal(0, root.GetProperty("chats_without_shutdown").GetInt64());
        Assert.Equal(1, root.GetProperty("compactions").GetProperty("by_trigger")[0].GetProperty("count").GetInt64());
        Assert.Equal(100, root.GetProperty("compactions").GetProperty("median_pre_compaction_tokens").GetInt64());
        Assert.Equal(500, root.GetProperty("shutdown_totals").GetProperty("nano_aiu").GetInt64());
        Assert.Equal(A, root.GetProperty("top_chats_by_compactions")[0].GetProperty("session_id").GetString());
        Assert.False(root.TryGetProperty("from", out _));
        Assert.DoesNotContain("SECRET_PATH", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_rollup_json_omits_medians_without_compactions()
    {
        using var fx = new FixtureStore();

        using var doc = JsonDocument.Parse(Run(fx, "rollup").Output);

        var compactions = doc.RootElement.GetProperty("compactions");
        Assert.Equal(0, compactions.GetProperty("count").GetInt64());
        Assert.False(compactions.TryGetProperty("median_pre_compaction_tokens", out _));
        Assert.Equal(0, doc.RootElement.GetProperty("chats_considered").GetInt64());
    }

    [Fact]
    public void Cli_rollup_text_cleans_control_characters_and_prints_no_paths()
    {
        using var fx = new FixtureStore();
        AddChat(fx, A, 10);
        fx.WriteEvents(A, new[]
        {
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"edit\\u202Eevil\\u001b[2J\",\"args\":{\"path\":\"SECRET_PATH\"}}}",
            "{\"type\":\"user.message\",\"data\":{\"message\":\"SECRET_MESSAGE\"}}",
            Compaction("ctx\\nfake line", 100, 40),
            Shutdown(2, 500, 4, 1),
        });

        var (exit, output) = Run(fx, "rollup", "--text");

        Assert.Equal(0, exit);
        Assert.StartsWith("rollup: 1 chat(s) considered", output);
        Assert.Contains("edit?evil\u241B[2J", output);
        Assert.Contains("ctx fake line", output);
        Assert.DoesNotContain("\u202E", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_PATH", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_MESSAGE", output, StringComparison.Ordinal);
        Assert.DoesNotContain(fx.Root, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("shutdown totals: premium requests 2, nano_aiu 500, changes +4/-1", output);
        Assert.Contains("coverage: " + Caps.Coverage, output);
    }

    [Fact]
    public void Cli_help_lists_rollup()
    {
        Assert.Contains("rollup [--from DATE] [--to DATE] [--limit N]", CliRunner.Usage);
    }

    private static (int Exit, string Output) Run(FixtureStore fx, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), stdout, stderr);
        return (exit, stdout.ToString());
    }
}
