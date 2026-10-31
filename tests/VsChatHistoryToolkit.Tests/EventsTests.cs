using System.Text;
using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class EventsTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Events_summarizes_selected_metadata_without_returning_payloads()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        var lines = new[]
        {
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"Edit\",\"args\":{\"path\":\"SECRET_PATH\"},\"message\":\"SECRET_MESSAGE\"}}",
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"\",\"name\":\"Read\",\"args\":{\"path\":\"SECRET_PATH\"}}}",
            "{\"type\":\"permission.requested\",\"data\":{\"permissionRequest\":{\"kind\":\"write\"}}}",
            "{\"type\":\"permission.requested\",\"data\":{\"request\":{\"kind\":\"read\"}}}",
            "{\"type\":\"skill.invoked\",\"data\":{\"name\":\"review\"}}",
            "{\"type\":\"abort\"}",
            "{\"type\":\"subagent.started\"}",
            "{\"type\":\"session.compaction_complete\",\"data\":{\"checkpointNumber\":7,\"trigger\":\"context\",\"success\":true,\"preCompactionTokens\":90,\"postCompactionTokens\":40,\"tokensRemoved\":50,\"messagesRemoved\":3,\"tokenLimit\":100}}",
            "{\"type\":\"session.shutdown\",\"data\":{\"shutdownType\":\"normal\",\"totalPremiumRequests\":2,\"totalNanoAiu\":500,\"codeChanges\":{\"linesAdded\":4,\"linesRemoved\":1,\"filesModified\":[\"SECRET_PATH\"]},\"currentTokens\":90,\"systemTokens\":10,\"conversationTokens\":70,\"toolDefinitionsTokens\":10,\"totalApiDurationMs\":1200,\"modelMetrics\":[{\"model\":\"model-a\",\"requests\":{\"count\":2,\"cost\":1.25}}]}}",
            "{\"type\":\"user.message\",\"data\":{\"message\":\"SECRET_MESSAGE\"}}",
        };
        var raw = string.Join('\n', lines) + "\n";
        fx.WriteEventsRaw(A, raw);

        var result = new ChatStore(fx.Paths).Events(A);

        Assert.True(result.Present);
        Assert.Null(result.Note);
        Assert.True(result.Scan.Complete);
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), result.Scan.BytesScanned);
        Assert.Equal(lines.Length, result.Scan.LinesScanned);
        Assert.Equal(2, result.EventCounts.Single(x => x.Type == "tool.execution_start").Count);
        Assert.Equal(1, result.EventCounts.Single(x => x.Type == "user.message").Count);
        Assert.Equal(new[] { "Edit", "Read" }, result.Tools.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name));
        Assert.Equal(new[] { "read", "write" }, result.PermissionKinds.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name));
        Assert.Equal("review", Assert.Single(result.Skills).Name);
        Assert.Equal(1, result.Aborts);
        Assert.Equal(1, result.SubagentsStarted);
        var compaction = Assert.Single(result.Compactions);
        Assert.Equal(7, compaction.CheckpointNumber);
        Assert.Equal(50, compaction.TokensRemoved);
        var shutdown = Assert.Single(result.Shutdowns);
        Assert.Equal(1, shutdown.FilesModified);
        Assert.Equal(70, shutdown.ConversationTokens);
        Assert.Equal(2, shutdown.Models![0].Requests!.Count);
        Assert.Equal(1.25m, shutdown.Models[0].Requests!.Cost);

        var json = JsonOut.Serialize(result);
        Assert.DoesNotContain("SECRET_PATH", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_MESSAGE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"args\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Events_missing_file_is_normal_but_unknown_id_is_not_found()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);

        var result = new ChatStore(fx.Paths).Events(A);

        Assert.False(result.Present);
        Assert.Contains("No events.jsonl", result.Note);
        Assert.True(result.Scan.Complete);
        Assert.Equal(0, result.Scan.BytesScanned);
        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Events("22222222-2222-2222-2222-222222222222"));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Events_handles_utf8_bom_type_after_nested_data_and_mixed_line_endings()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.WriteEventsRaw(A, "\uFEFF{\"data\":{\"ignored\":{\"type\":\"not-an-event-type\"}},\"type\":\"abort\"}\r\n{\"type\":\"abort\"}\r{\"type\":\"skill.invoked\",\"data\":{\"name\":\"review\"}}");

        var result = new ChatStore(fx.Paths).Events(A);

        Assert.True(result.Scan.Complete);
        Assert.Equal(3, result.Scan.LinesScanned);
        Assert.Equal(2, result.Aborts);
        Assert.Equal("review", Assert.Single(result.Skills).Name);
    }

    [Fact]
    public void Events_counts_malformed_types_but_does_not_count_them_as_valid_events()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.WriteEventsRaw(A, "{\"type\":\"abort\",}\n{\"type\":\"session.compaction_complete\",\"data\":{},}");

        var result = new ChatStore(fx.Paths).Events(A);

        Assert.Equal(1, result.EventCounts.Single(x => x.Type == "abort").Count);
        Assert.Equal(1, result.EventCounts.Single(x => x.Type == "session.compaction_complete").Count);
        Assert.Equal(0, result.Aborts);
        Assert.Equal(0, result.CompactionCount);
        Assert.Empty(result.Compactions);
        Assert.Empty(result.Shutdowns);
    }

    [Fact]
    public void Events_reports_a_fixed_store_error_when_the_file_cannot_be_read()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.WriteEvents(A, new[] { "{\"type\":\"abort\"}" });
        using var locked = new FileStream(fx.EventsPath(A), FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<ToolkitException>(() => new ChatStore(fx.Paths).Events(A));

        Assert.Equal(ErrorKind.StoreUnavailable, ex.Kind);
        Assert.Equal(ExitCodes.StoreUnavailable, ex.ExitCode);
        Assert.Equal("Cannot read events.jsonl.", ex.Message);
        Assert.DoesNotContain(fx.Root, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Events_counts_but_does_not_parse_a_line_over_the_line_cap()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        var longLine = "{\"type\":\"abort\",\"data\":{\"padding\":\"" + new string('x', Caps.EventsMaxLineBytes) + "\"}}";
        fx.WriteEventsRaw(A, longLine + "\n{\"type\":\"abort\"}");

        var result = new ChatStore(fx.Paths).Events(A);

        Assert.False(result.Scan.Complete);
        Assert.Equal(2, result.Scan.LinesScanned);
        Assert.Equal(1, result.Aborts);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Events_stop_at_the_byte_and_line_caps()
    {
        using (var fx = new FixtureStore())
        {
            fx.AddSession(A);
            fx.WriteEventsRaw(A, new string('x', Caps.EventsMaxBytes + 20));
            var bytes = new ChatStore(fx.Paths).Events(A);
            Assert.Equal(Caps.EventsMaxBytes, bytes.Scan.BytesScanned);
            Assert.False(bytes.Scan.Complete);
            Assert.True(bytes.Truncated);
        }

        using (var fx = new FixtureStore())
        {
            fx.AddSession(A);
            fx.WriteEventsRaw(A, string.Join('\n', Enumerable.Repeat("{}", Caps.EventsMaxLines + 1)));
            var lines = new ChatStore(fx.Paths).Events(A);
            Assert.Equal(Caps.EventsMaxLines, lines.Scan.LinesScanned);
            Assert.False(lines.Scan.Complete);
            Assert.True(lines.Truncated);
        }
    }

    [Fact]
    public void Events_caps_top_lists_and_detail_arrays()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        var lines = Enumerable.Range(0, Caps.EventsTop + 1).Select(i => "{\"type\":\"custom." + i + "\"}")
            .Concat(Enumerable.Repeat("{\"type\":\"session.compaction_complete\",\"data\":{}}", Caps.EventsCompactions + 1))
            .Concat(Enumerable.Repeat("{\"type\":\"session.shutdown\",\"data\":{}}", Caps.EventsShutdowns + 1));
        fx.WriteEvents(A, lines);

        var result = new ChatStore(fx.Paths).Events(A);

        Assert.Equal(Caps.EventsTop, result.EventCounts.Count);
        Assert.Equal(Caps.EventsCompactions + 1, result.CompactionCount);
        Assert.Equal(Caps.EventsCompactions, result.Compactions.Count);
        Assert.Equal(Caps.EventsShutdowns, result.Shutdowns.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Events_omits_missing_fields_and_text_cleans_control_characters()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.WriteEvents(A, new[]
        {
            "{\"type\":\"session.compaction_complete\",\"data\":{\"success\":true}}",
            "{\"type\":\"tool.execution_start\",\"data\":{\"toolName\":\"edit\\u202Eevil\",\"args\":{\"path\":\"SECRET_PATH\"}}}",
        });
        var (exit, output) = Run(fx, "events", A, "--text");
        Assert.Equal(0, exit);
        Assert.DoesNotContain("\u202E", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_PATH", output, StringComparison.Ordinal);
        Assert.Contains("edit?evil", output);
        using var doc = JsonDocument.Parse(Run(fx, "events", A).Output);
        var compaction = doc.RootElement.GetProperty("compactions")[0];
        Assert.True(compaction.GetProperty("success").GetBoolean());
        Assert.False(compaction.TryGetProperty("checkpoint_number", out _));
    }

    [Fact]
    public void Cli_events_json_has_coverage_and_missing_file_note()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);

        var (exit, output) = Run(fx, "events", A);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("events", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.False(root.GetProperty("present").GetBoolean());
        Assert.True(root.TryGetProperty("note", out _));
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Cli_events_requires_one_id()
    {
        using var fx = new FixtureStore();
        Assert.Equal(ExitCodes.Usage, Run(fx, "events").Exit);
        Assert.Equal(ExitCodes.Usage, Run(fx, "events", A, "extra").Exit);
    }

    private static (int Exit, string Output) Run(FixtureStore fx, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliRunner.Run(args.Concat(new[] { "--root", fx.Root }).ToArray(), stdout, stderr);
        return (exit, stdout.ToString());
    }
}
