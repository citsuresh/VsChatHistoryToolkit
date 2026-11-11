using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class UsageTurnsCliTests
{
    private const string Id = "synthetic-private-session";

    private static (int Exit, string Out, string Err) Run(FixtureStore fx, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliRunner.Run(args, stdout, stderr, new CliEnvironment(fx.Root, fx.LogDir));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Json_has_only_numeric_usage_and_timestamp_fields_with_no_identifying_strings(bool withMultiplier)
    {
        using var fx = new FixtureStore();
        fx.AddSession(Id, summary: "PRIVATE TITLE", repo: "PRIVATE REPO", cwd: @"C:\PRIVATE PATH");
        fx.AddUsageTable(withMultiplier: withMultiplier);
        fx.AddUsage(Id, 140, "PRIVATE MODEL", 500, 20, multiplier: withMultiplier ? 7.5 : null,
            createdAt: "2026-01-02T03:04:05.123+02:00");
        fx.AddTurn(Id, 140, "PRIVATE MESSAGE", "PRIVATE RESPONSE", "2025-01-01T00:00:00Z");
        fx.WriteYaml(Id, FixtureStore.SampleYaml(Id, "PRIVATE YAML TITLE"));
        var before = File.ReadAllBytes(fx.DbPath);
        var yamlBefore = File.ReadAllBytes(fx.YamlPath(Id));

        var (exit, output, error) = Run(fx, "usage-turns", Id);

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.Equal(before, File.ReadAllBytes(fx.DbPath));
        Assert.Equal(yamlBefore, File.ReadAllBytes(fx.YamlPath(Id)));
        Assert.DoesNotContain("PRIVATE", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Id, output, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(new[] { "by_turn", "command", "count", "coverage", "limit", "ok", "recorded", "total", "truncated" },
            root.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal("usage-turns", root.GetProperty("command").GetString());
        Assert.Equal(Caps.Coverage, root.GetProperty("coverage").GetString());
        Assert.True(root.GetProperty("recorded").GetBoolean());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(20, root.GetProperty("limit").GetInt32());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        var turn = Assert.Single(root.GetProperty("by_turn").EnumerateArray());
        Assert.Equal(new[] { "first_at", "last_at", "peak_input_tokens", "timestamps_complete", "totals", "turn_index" },
            turn.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(140, turn.GetProperty("turn_index").GetInt64());
        Assert.Equal(500, turn.GetProperty("peak_input_tokens").GetInt64());
        Assert.Equal("2026-01-02T01:04:05.123Z", turn.GetProperty("first_at").GetString());
        Assert.Equal(turn.GetProperty("first_at").GetString(), turn.GetProperty("last_at").GetString());
        Assert.True(turn.GetProperty("timestamps_complete").GetBoolean());
        var names = new[] { "requests", "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens", "reasoning_tokens", "nano_aiu", "duration_ms" }
            .Concat(withMultiplier ? new[] { "credits" } : Array.Empty<string>()).Order();
        foreach (var totals in new[] { root.GetProperty("total"), turn.GetProperty("totals") })
        {
            Assert.Equal(names, totals.EnumerateObject().Select(x => x.Name).Order());
            Assert.All(totals.EnumerateObject(), x => Assert.Equal(JsonValueKind.Number, x.Value.ValueKind));
            if (withMultiplier) Assert.Equal(7.5, totals.GetProperty("credits").GetDouble());
        }
    }

    [Fact]
    public void Numeric_command_works_without_message_tables_or_session_metadata_or_workspace_files()
    {
        using var fx = new FixtureStore();
        fx.AddSession(Id);
        fx.AddUsageTable();
        fx.AddUsage(Id, 120, "m", 10, 5);
        fx.Exec("""
            DROP TABLE turns;
            DROP TABLE checkpoints;
            DROP TABLE session_files;
            DROP TABLE search_index;
            ALTER TABLE sessions DROP COLUMN summary;
            ALTER TABLE sessions DROP COLUMN repository;
            ALTER TABLE sessions DROP COLUMN cwd;
            ALTER TABLE assistant_usage_events DROP COLUMN model;
            """);

        var (exit, output, _) = Run(fx, "usage-turns", Id);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(120, doc.RootElement.GetProperty("by_turn")[0].GetProperty("turn_index").GetInt64());
        Assert.False(Directory.Exists(fx.SessionDir(Id)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Text_output_contains_numeric_usage_and_event_times_but_no_id_or_model(bool withMultiplier)
    {
        using var fx = new FixtureStore();
        fx.AddSession(Id);
        fx.AddUsageTable(withMultiplier: withMultiplier);
        fx.AddUsage(Id, 101, "PRIVATE MODEL", 10, 5, multiplier: withMultiplier ? 1 : null,
            createdAt: "2026-01-01T00:00:00Z", agentId: "PRIVATE AGENT");

        var (exit, output, error) = Run(fx, "usage-turns", Id, "--text");

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.Contains("usage-turns: 1 turn(s), newest 20, ascending turn_index", output);
        Assert.Contains("total (all session requests): 1 requests", output);
        Assert.Contains("turn 101:", output);
        Assert.Contains("main peak in -, first 2026-01-01T00:00:00.000Z, last 2026-01-01T00:00:00.000Z, timestamps complete true", output);
        Assert.DoesNotContain(Id, output, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", output, StringComparison.Ordinal);
        Assert.Equal(withMultiplier, output.Contains("credits", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--limit", "1")]
    [InlineData("--limit=1", null)]
    public void Limit_can_be_before_the_verb_and_a_limited_result_has_complete_totals(string option, string? value)
    {
        using var fx = new FixtureStore();
        fx.AddSession(Id);
        fx.AddUsageTable();
        fx.AddUsage(Id, 100, "m", 10, 5);
        fx.AddUsage(Id, 170, "m", 20, 6);
        var args = value is null ? new[] { option, "usage-turns", Id } : new[] { option, value, "usage-turns", Id };

        var (exit, output, _) = Run(fx, args);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("limit").GetInt32());
        Assert.Equal(2, root.GetProperty("total").GetProperty("requests").GetInt64());
        Assert.Equal(170, Assert.Single(root.GetProperty("by_turn").EnumerateArray()).GetProperty("turn_index").GetInt64());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.TryGetProperty("note", out _));
    }

    [Theory]
    [InlineData("usage-turns")]
    [InlineData("usage-turns", Id, "extra")]
    [InlineData("usage-turns", "..\\invalid")]
    [InlineData("usage-turns", Id, "--limit")]
    [InlineData("usage-turns", Id, "--limit", "0")]
    [InlineData("usage-turns", Id, "--limit", "-1")]
    [InlineData("usage-turns", Id, "--limit", "1.5")]
    [InlineData("usage-turns", Id, "--limit", "2147483648")]
    [InlineData("usage-turns", Id, "--limit", "bad")]
    [InlineData("usage-turns", Id, "--by-turn")]
    [InlineData("usage-turns", Id, "--from", "2026-01-01")]
    [InlineData("usage-turns", Id, "--to", "2026-01-01")]
    [InlineData("usage-turns", Id, "--repo", "synthetic")]
    [InlineData("usage-turns", Id, "--model", "synthetic")]
    [InlineData("usage-turns", Id, "--group-by", "day")]
    [InlineData("usage-turns", Id, "--range", "0-3")]
    [InlineData("usage-turns", Id, "--index", "0")]
    [InlineData("usage-turns", Id, "--force")]
    [InlineData("usage-turns", Id, "--text=false")]
    [InlineData("usage-turns", Id, "--help=true")]
    [InlineData("usage-turns", Id, "--unknown")]
    public void Bad_arguments_are_usage_errors_without_echoing_the_session_id(params string[] args)
    {
        using var fx = new FixtureStore();

        var (exit, output, _) = Run(fx, args);

        Assert.Equal(ExitCodes.Usage, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("usage", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(Id, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_session_is_not_found_without_echoing_its_id()
    {
        using var fx = new FixtureStore();

        var (exit, output, _) = Run(fx, "usage-turns", Id);

        Assert.Equal(ExitCodes.NotFound, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal("not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(Id, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_usage_errors_use_stderr_and_not_stdout()
    {
        using var fx = new FixtureStore();

        var (exit, output, error) = Run(fx, "usage-turns", Id, "--limit", "0", "--text");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Empty(output);
        Assert.Contains("error (usage)", error);
        Assert.DoesNotContain(Id, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_usage_is_explicit_and_help_never_needs_a_store()
    {
        using var fx = new FixtureStore();
        fx.AddSession(Id);
        var (exit, output, _) = Run(fx, "usage-turns", Id);
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("recorded").GetBoolean());
        Assert.True(doc.RootElement.TryGetProperty("note", out _));
        Assert.False(doc.RootElement.TryGetProperty("total", out _));
        Assert.Empty(doc.RootElement.GetProperty("by_turn").EnumerateArray());

        using var missing = new FixtureStore(createDb: false);
        var help = Run(missing, "usage-turns", "--help");
        Assert.Equal(0, help.Exit);
        Assert.Contains("usage-turns <id> [--limit N]", help.Out);
        Assert.Equal(ExitCodes.StoreUnavailable, Run(missing, "usage-turns", Id).Exit);
    }

    [Fact]
    public void Schema_guard_still_applies_and_other_verbs_keep_their_existing_option_behavior()
    {
        using var fx = new FixtureStore(schemaVersion: 7);
        Assert.Equal(ExitCodes.StoreUnavailable, Run(fx, "usage-turns", Id).Exit);
        Assert.True(ParsedArgs.Parse(new[] { "usage", Id, "--by-turn" }).Flag("by-turn"));
        Assert.True(ParsedArgs.Parse(new[] { "usage", Id, "--text=false" }).Flag("text"));
        Assert.Equal("requests", ParsedArgs.Parse(new[] { "usage-top", "--by", "requests" }).Option("by"));
    }
}
