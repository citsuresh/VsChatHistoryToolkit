using System.Text.Json;
using VsChatHistoryToolkit;
using VsChatHistoryToolkit.Cli;

namespace VsChatHistoryToolkit.Tests;

public class CliTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";

    private static (int Exit, string Out, string Err) Run(FixtureStore fx, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var exit = CliRunner.Run(args, o, e, new CliEnvironment(fx.Root, fx.LogDir, () => FixtureStore.Now));
        return (exit, o.ToString().TrimEnd(), e.ToString().TrimEnd());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Every_json_result_carries_coverage_and_truncated()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.AddCheckpoint(A, 1, "cp");

        foreach (var args in new[]
        {
            new[] { "search", "hello" },
            new[] { "sessions" },
            new[] { "show", A },
            new[] { "turns", A },
            new[] { "checkpoints", A },
            new[] { "stats" },
        })
        {
            var (exit, output, _) = Run(fx, args);
            Assert.Equal(0, exit);
            var json = Json(output);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("session-store.db only", json.GetProperty("coverage").GetString());
            Assert.Equal(JsonValueKind.False, json.GetProperty("truncated").ValueKind);
            Assert.Equal(args[0], json.GetProperty("command").GetString());
        }
    }

    [Fact]
    public void Search_json_shape()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);

        var json = Json(Run(fx, "search", "hello").Out);

        var hit = json.GetProperty("hits")[0];
        Assert.Equal(A, hit.GetProperty("session_id").GetString());
        Assert.Equal("turn", hit.GetProperty("source_type").GetString());
        Assert.True(hit.TryGetProperty("snippet", out _));
        Assert.Equal(1, json.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Text_mode_prints_coverage_line()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        var (exit, output, _) = Run(fx, "sessions", "--text");
        Assert.Equal(0, exit);
        Assert.Contains("coverage: session-store.db only", output);
    }

    [Theory]
    [InlineData(1, new[] { "bogus" })]
    [InlineData(1, new[] { "search" })]
    [InlineData(1, new[] { "show" })]
    [InlineData(1, new[] { "sessions", "--limit", "abc" })]
    [InlineData(1, new[] { "sessions", "--nope" })]
    [InlineData(1, new[] { "turns", "11111111-1111-1111-1111-111111111111", "--range", "5" })]
    [InlineData(1, new[] { "rename", "11111111-1111-1111-1111-111111111111" })]
    [InlineData(1, new[] { "rename", "11111111-1111-1111-1111-111111111111", "   " })]
    [InlineData(2, new[] { "show", "99999999-9999-9999-9999-999999999999" })]
    [InlineData(2, new[] { "undo-rename", "99999999-9999-9999-9999-999999999999" })]
    public void Errors_use_stable_exit_codes_and_json_errors(int expectedExit, string[] args)
    {
        using var fx = new FixtureStore();
        var (exit, output, _) = Run(fx, args);
        Assert.Equal(expectedExit, exit);
        var json = Json(output);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("session-store.db only", json.GetProperty("coverage").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("error").GetProperty("code").GetString()));
    }

    [Fact]
    public void Schema_mismatch_exit_code_3_and_names_version()
    {
        using var fx = new FixtureStore(schemaVersion: 6);
        var (exit, output, _) = Run(fx, "stats");
        Assert.Equal(3, exit);
        Assert.Equal("schema_mismatch", Json(output).GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("6", output);
    }

    [Fact]
    public void Text_mode_errors_go_to_stderr()
    {
        using var fx = new FixtureStore();
        var (exit, output, err) = Run(fx, "show", "99999999-9999-9999-9999-999999999999", "--text");
        Assert.Equal(2, exit);
        Assert.Equal("", output);
        Assert.Contains("not_found", err);
    }

    [Fact]
    public void Rename_active_session_exits_4_and_force_succeeds()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        File.SetLastWriteTimeUtc(fx.YamlPath(A), FixtureStore.Now.UtcDateTime.AddMinutes(-1));

        Assert.Equal(4, Run(fx, "rename", A, "New name").Exit);

        var (exit, output, _) = Run(fx, "rename", A, "New name", "--force");
        Assert.Equal(0, exit);
        var json = Json(output);
        Assert.True(json.GetProperty("applied").GetBoolean());
        Assert.True(json.GetProperty("forced").GetBoolean());
        Assert.Equal(SessionRenamer.RestartNote, json.GetProperty("note").GetString());
    }

    [Fact]
    public void Rename_text_output_ends_with_restart_note_and_undo_works()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        var original = File.ReadAllBytes(fx.YamlPath(A));

        var (exit, output, _) = Run(fx, "rename", A, "Title with: colon", "--text");
        Assert.Equal(0, exit);
        Assert.EndsWith(SessionRenamer.RestartNote, output);

        var undo = Run(fx, "undo-rename", A, "--text");
        Assert.Equal(0, undo.Exit);
        Assert.EndsWith(SessionRenamer.RestartNote, undo.Out);
        Assert.Equal(original, File.ReadAllBytes(fx.YamlPath(A)));
    }

    [Fact]
    public void Rename_dry_run_reports_plan()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        var json = Json(Run(fx, "rename", A, "Planned", "--dry-run").Out);
        Assert.True(json.GetProperty("dry_run").GetBoolean());
        Assert.False(json.GetProperty("applied").GetBoolean());
        Assert.Equal("Planned", json.GetProperty("new_title").GetString());
    }

    [Fact]
    public void Title_starting_with_dashes_needs_double_dash_separator()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        Assert.Equal(1, Run(fx, "rename", A, "--bad title").Exit);
        Assert.Equal(0, Run(fx, "rename", A, "--dry-run", "--", "--bad title").Exit);
    }

    [Fact]
    public void Help_exits_zero()
    {
        using var fx = new FixtureStore();
        var (exit, output, _) = Run(fx, "--help");
        Assert.Equal(0, exit);
        Assert.Contains("chathistory", output);
    }
}
