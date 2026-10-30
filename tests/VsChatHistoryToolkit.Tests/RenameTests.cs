using System.Text;
using Microsoft.Data.Sqlite;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Tests;

public class RenameTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";

    private static SessionRenamer Renamer(FixtureStore fx) =>
        new(fx.Paths, fx.LogDir, () => FixtureStore.Now, busyTimeoutMs: 200);

    private static string Summary(FixtureStore fx, string id)
    {
        using var c = fx.OpenWriter();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT summary FROM sessions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return (string)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void Rename_changes_only_name_user_named_and_summary()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var before = File.ReadAllText(fx.YamlPath(A));

        var result = Renamer(fx).Rename(A, "  New title  ", force: false, dryRun: false);

        Assert.True(result.Applied);
        Assert.Equal("Original title", result.OldTitle);
        Assert.Equal("New title", result.NewTitle);
        Assert.Equal(SessionRenamer.RestartNote, result.Note);
        Assert.Equal(before.Replace("name: Original title", "name: New title").Replace("user_named: false", "user_named: true"),
            File.ReadAllText(fx.YamlPath(A)));
        Assert.Equal("New title", Summary(fx, A));
        Assert.Empty(Directory.GetFiles(fx.SessionDir(A), "*.tmp"));
    }

    [Fact]
    public void Rename_does_not_touch_other_sessions_or_updated_at()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "a summary");
        fx.AddIdleSession(B, summary: "b summary");
        var otherYaml = File.ReadAllBytes(fx.YamlPath(B));

        Renamer(fx).Rename(A, "Renamed", false, false);

        Assert.Equal("b summary", Summary(fx, B));
        Assert.Equal(otherYaml, File.ReadAllBytes(fx.YamlPath(B)));
        using var c = fx.OpenWriter();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT updated_at FROM sessions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", A);
        Assert.Equal("2026-01-05T10:00:00.000Z", cmd.ExecuteScalar());
    }

    [Fact]
    public void Rename_inserts_missing_keys_and_undo_restores_exact_bytes()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "old summary");
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.000Z");
        var original = Encoding.UTF8.GetBytes("id: x\nbranch: main");
        fx.WriteYaml(A, original);

        var r = Renamer(fx);
        r.Rename(A, "Fresh name", false, false);
        Assert.Equal("id: x\nbranch: main\nname: Fresh name\nuser_named: true", File.ReadAllText(fx.YamlPath(A)));

        var undo = r.Undo(A, false, false);
        Assert.True(undo.Applied);
        Assert.Equal(original, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("old summary", Summary(fx, A));
        Assert.Equal(SessionRenamer.RestartNote, undo.Note);
    }

    [Fact]
    public void Undo_restores_existing_lines_and_second_undo_finds_nothing()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var original = File.ReadAllBytes(fx.YamlPath(A));
        var r = Renamer(fx);

        r.Rename(A, "Changed: with colon", false, false);
        Assert.Contains("name: \"Changed: with colon\"", File.ReadAllText(fx.YamlPath(A)));

        r.Undo(A, false, false);
        Assert.Equal(original, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("old summary", Summary(fx, A));

        Assert.Equal(ErrorKind.NotFound, Assert.Throws<ToolkitException>(() => r.Undo(A, false, false)).Kind);
    }

    [Fact]
    public void Two_renames_undo_one_step_at_a_time()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "s0");
        var r = Renamer(fx);

        r.Rename(A, "One", false, false);
        var afterOne = File.ReadAllBytes(fx.YamlPath(A));
        r.Rename(A, "Two", false, false);

        r.Undo(A, false, false);
        Assert.Equal(afterOne, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("One", Summary(fx, A));
    }

    [Fact]
    public void Undo_refuses_when_state_changed_since_the_rename_unless_forced()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var r = Renamer(fx);
        r.Rename(A, "Mine", false, false);

        fx.Exec("UPDATE sessions SET summary='someone else' WHERE id=$id", ("$id", A));

        Assert.Equal(ErrorKind.Refused, Assert.Throws<ToolkitException>(() => r.Undo(A, false, false)).Kind);
        Assert.Equal("someone else", Summary(fx, A));

        r.Undo(A, force: true, dryRun: false);
        Assert.Equal("old summary", Summary(fx, A));
    }

    [Fact]
    public void Own_write_does_not_block_undo_but_a_later_touch_by_someone_else_does()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var r = Renamer(fx);
        r.Rename(A, "Mine", false, false);

        // Our own write is recent by the file clock, yet undo is allowed.
        Assert.True(r.Undo(A, false, dryRun: true).DryRun);

        File.SetLastWriteTimeUtc(fx.YamlPath(A), FixtureStore.Now.UtcDateTime.AddMinutes(-1));
        var ex = Assert.Throws<ToolkitException>(() => r.Undo(A, false, false));
        Assert.Equal(ErrorKind.Refused, ex.Kind);
    }

    [Fact]
    public void Undo_without_any_log_is_not_found()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        Assert.Equal(ErrorKind.NotFound, Assert.Throws<ToolkitException>(() => Renamer(fx).Undo(A, false, false)).Kind);
    }

    [Fact]
    public void Log_records_old_and_new_values_before_and_after_the_write()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        Renamer(fx).Rename(A, "Logged", false, false);

        var lines = File.ReadAllLines(fx.LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"status\":\"pending\"", lines[0]);
        Assert.Contains("\"status\":\"applied\"", lines[1]);
        Assert.Contains("name: Original title", lines[0]);
        Assert.Contains("name: Logged", lines[0]);
        Assert.Contains("old summary", lines[0]);
    }

    [Fact]
    public void The_printed_result_caps_long_summaries_but_the_log_and_undo_keep_them_whole()
    {
        using var fx = new FixtureStore();
        var longSummary = new string('s', 5000);
        fx.AddIdleSession(A, summary: longSummary);
        var r = Renamer(fx);

        var result = r.Rename(A, "Short new title", false, false);

        Assert.True(result.Truncated);
        Assert.True(result.Before.Summary!.Length <= Caps.SnippetChars + 1);
        Assert.Equal("Short new title", result.After.Summary);
        Assert.Contains(longSummary, File.ReadAllText(fx.LogPath));

        var undo = r.Undo(A, false, false);

        Assert.True(undo.Applied);
        Assert.True(undo.After.Summary!.Length <= Caps.SnippetChars + 1);
        Assert.Equal(longSummary, Summary(fx, A));
    }

    [Fact]
    public void A_result_with_only_short_values_is_not_truncated()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "short");
        Assert.False(Renamer(fx).Rename(A, "Also short", false, dryRun: true).Truncated);
    }
    [Fact]
    public void Dry_run_changes_nothing_and_writes_no_log()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var bytes = File.ReadAllBytes(fx.YamlPath(A));

        var result = Renamer(fx).Rename(A, "Planned", false, dryRun: true);

        Assert.True(result.DryRun);
        Assert.False(result.Applied);
        Assert.Equal("Planned", result.NewTitle);
        Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("old summary", Summary(fx, A));
        Assert.False(File.Exists(fx.LogPath));
    }

    [Fact]
    public void Rename_preserves_bom_and_crlf()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "s");
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.000Z");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("id: x\r\nname: Old\r\nuser_named: false\r\nbranch: main\r\n")).ToArray();
        fx.WriteYaml(A, bytes);

        Renamer(fx).Rename(A, "New", false, false);

        var expected = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("id: x\r\nname: New\r\nuser_named: true\r\nbranch: main\r\n")).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(fx.YamlPath(A)));
    }

    // ---- refusal and force ----

    [Theory]
    [InlineData("workspace.yaml")]
    [InlineData("events.jsonl")]
    public void Recently_modified_files_make_the_session_active(string file)
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        fx.WriteEvents(A, new[] { "{}" });
        var path = Path.Combine(fx.SessionDir(A), file);
        File.SetLastWriteTimeUtc(path, FixtureStore.Now.UtcDateTime.AddMinutes(-3));
        var before = File.ReadAllBytes(fx.YamlPath(A));

        var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "Nope", false, false));

        Assert.Equal(ErrorKind.Refused, ex.Kind);
        Assert.Equal(ExitCodes.Refused, ex.ExitCode);
        Assert.Contains(file, ex.Message);
        Assert.Equal(before, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.False(File.Exists(fx.LogPath));
    }

    [Fact]
    public void A_recent_turn_makes_the_session_active_and_force_overrides()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "s");
        fx.AddTurn(A, 0, "u", "a", FixtureStore.Now.AddMinutes(-2).ToString("o"));
        fx.WriteYaml(A, FixtureStore.SampleYaml(A));

        Assert.Equal(ErrorKind.Refused, Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false)).Kind);

        var forced = Renamer(fx).Rename(A, "X", force: true, dryRun: false);
        Assert.True(forced.Applied);
        Assert.True(forced.Forced);
        Assert.Contains(forced.ActiveSignals, s => s.StartsWith("last turn"));
    }

    [Fact]
    public void Files_older_than_the_window_are_not_active()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        File.SetLastWriteTimeUtc(fx.YamlPath(A), FixtureStore.Now.UtcDateTime.AddMinutes(-11));
        Assert.True(Renamer(fx).Rename(A, "Fine", false, false).Applied);
    }

    // ---- missing things and locks ----

    [Fact]
    public void Missing_session_folder_is_not_found_and_nothing_changes()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A, summary: "row only");

        var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false));

        Assert.Equal(ErrorKind.NotFound, ex.Kind);
        Assert.Equal("row only", Summary(fx, A));
    }

    [Fact]
    public void Missing_workspace_yaml_is_not_found()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        Directory.CreateDirectory(fx.SessionDir(A));
        Assert.Equal(ErrorKind.NotFound, Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false)).Kind);
    }

    [Fact]
    public void Missing_database_row_is_not_found_and_yaml_is_untouched()
    {
        using var fx = new FixtureStore();
        fx.WriteYaml(B, FixtureStore.SampleYaml(B));
        var bytes = File.ReadAllBytes(fx.YamlPath(B));

        var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(B, "X", false, false));

        Assert.Equal(ErrorKind.NotFound, ex.Kind);
        Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(B)));
    }

    [Fact]
    public void Id_must_match_exactly_one_session()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A);
        Assert.Equal(ErrorKind.Usage, Assert.Throws<ToolkitException>(() => Renamer(fx).Rename("*", "X", false, false)).Kind);
        Assert.Equal(ErrorKind.NotFound, Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A[..8], "X", false, false)).Kind);
    }

    [Fact]
    public void A_locked_database_fails_cleanly_with_nothing_half_applied()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var bytes = File.ReadAllBytes(fx.YamlPath(A));

        using (var blocker = fx.OpenWriter())
        {
            using (var c = blocker.CreateCommand()) { c.CommandText = "BEGIN IMMEDIATE"; c.ExecuteNonQuery(); }

            var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "Locked out", false, false));

            Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
            Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
            Assert.Contains("Nothing was changed", ex.Message);
            Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(A)));
            using (var c = blocker.CreateCommand()) { c.CommandText = "ROLLBACK"; c.ExecuteNonQuery(); }
        }

        Assert.Equal("old summary", Summary(fx, A));
        var log = File.ReadAllLines(fx.LogPath);
        Assert.Contains("\"status\":\"failed\"", log[^1]);

        // The failed attempt must not be offered for undo, and a retry once the lock is gone works.
        Assert.Equal(ErrorKind.NotFound, Assert.Throws<ToolkitException>(() => Renamer(fx).Undo(A, false, false)).Kind);
        Assert.True(Renamer(fx).Rename(A, "Locked out", false, false).Applied);
    }

    [Fact]
    public void A_yaml_write_failure_rolls_the_database_back()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var bytes = File.ReadAllBytes(fx.YamlPath(A));

        // Holding the file open without delete sharing makes the atomic replace fail.
        using (new FileStream(fx.YamlPath(A), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "Blocked", false, false));
            Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        }

        Assert.Equal("old summary", Summary(fx, A));
        Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Empty(Directory.GetFiles(fx.SessionDir(A), "*.tmp"));
    }

    [Fact]
    public void A_yaml_edit_between_reading_and_locking_is_detected_and_not_overwritten()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var r = Renamer(fx);
        byte[]? edited = null;
        r.StageHook = stage =>
        {
            if (stage != "after-lock") return;
            File.AppendAllText(fx.YamlPath(A), "extra: added by someone else\n");
            edited = File.ReadAllBytes(fx.YamlPath(A));
        };

        var ex = Assert.Throws<ToolkitException>(() => r.Rename(A, "Mine", false, false));

        Assert.Equal(ErrorKind.Refused, ex.Kind);
        Assert.Contains("changed while the rename was being prepared", ex.Message);
        Assert.Equal(edited, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("old summary", Summary(fx, A));
        Assert.Contains("\"status\":\"failed\"", File.ReadAllLines(fx.LogPath)[^1]);
    }

    [Fact]
    public void A_failed_final_log_write_is_reported_as_a_warning()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var r = Renamer(fx);
        FileStream? hold = null;
        r.StageHook = stage =>
        {
            if (stage == "before-applied-log") hold = new FileStream(fx.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };

        ChangeResult result;
        try
        {
            result = r.Rename(A, "Applied anyway", false, false);
        }
        finally
        {
            hold?.Dispose();
        }

        Assert.True(result.Applied);
        var warning = Assert.Single(result.Warnings!);
        Assert.Contains("undo-log", warning);
        Assert.Equal("Applied anyway", Summary(fx, A));
        Assert.Contains("\"status\":\"pending\"", File.ReadAllLines(fx.LogPath)[^1]);
    }

    [Fact]
    public void An_edit_made_while_the_replace_is_being_retried_is_detected_and_not_overwritten()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var r = Renamer(fx);
        byte[]? edited = null;
        r.StageHook = stage =>
        {
            if (stage != "before-replace-retry" || edited is not null) return;
            File.AppendAllText(fx.YamlPath(A), "extra: added while the file was held\n");
            edited = File.ReadAllBytes(fx.YamlPath(A));
        };

        // Holding the file without delete sharing makes File.Replace fail, so the retry loop runs; writes stay allowed.
        using (new FileStream(fx.YamlPath(A), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var ex = Assert.Throws<ToolkitException>(() => r.Rename(A, "Mine", false, false));

            Assert.Equal(ErrorKind.Refused, ex.Kind);
            Assert.Contains("while the replace was being retried", ex.Message);
        }

        Assert.NotNull(edited);
        Assert.Equal(edited, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("old summary", Summary(fx, A));
        Assert.Empty(Directory.GetFiles(fx.SessionDir(A), "*.tmp"));
    }

    [Fact]
    public void A_file_held_exclusively_for_a_moment_is_retried_and_then_replaced()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var r = Renamer(fx);
        FileStream? hold = null;
        var retries = 0;
        r.StageHook = stage =>
        {
            if (stage == "before-replace") hold = new FileStream(fx.YamlPath(A), FileMode.Open, FileAccess.Read, FileShare.None);
            if (stage == "before-replace-retry" && ++retries == 2)
            {
                hold!.Dispose();
                hold = null;
            }
        };

        ChangeResult result;
        try
        {
            result = r.Rename(A, "Retried", false, false);
        }
        finally
        {
            hold?.Dispose();
        }

        Assert.True(result.Applied);
        Assert.True(retries >= 2);
        Assert.Contains("name: Retried", File.ReadAllText(fx.YamlPath(A)));
        Assert.Equal("Retried", Summary(fx, A));
        Assert.Empty(Directory.GetFiles(fx.SessionDir(A), "*.tmp"));
    }

    [Fact]
    public void A_file_held_exclusively_for_the_whole_retry_budget_fails_with_nothing_changed()
    {
        using var fx = new FixtureStore();
        fx.AddIdleSession(A, summary: "old summary");
        var bytes = File.ReadAllBytes(fx.YamlPath(A));
        var r = Renamer(fx);
        FileStream? hold = null;
        r.StageHook = stage =>
        {
            if (stage == "before-replace") hold = new FileStream(fx.YamlPath(A), FileMode.Open, FileAccess.Read, FileShare.None);
        };

        try
        {
            var ex = Assert.Throws<ToolkitException>(() => r.Rename(A, "Never", false, false));
            Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
            Assert.Contains("Nothing was changed", ex.Message);
        }
        finally
        {
            hold?.Dispose();
        }

        Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal("old summary", Summary(fx, A));
        Assert.Empty(Directory.GetFiles(fx.SessionDir(A), "*.tmp"));
    }

    [Fact]
    public void Duplicate_top_level_keys_are_refused_untouched()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.000Z");
        var bytes = Encoding.UTF8.GetBytes("id: x\nname: One\nname: Two\nuser_named: false\n");
        fx.WriteYaml(A, bytes);

        var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false));

        Assert.Equal(ErrorKind.Refused, ex.Kind);
        Assert.Contains("more than one", ex.Message);
        Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(A)));
    }

    [Fact]
    public void A_junction_for_the_session_folder_is_refused_and_the_target_is_untouched()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.000Z");

        var outside = Path.Combine(fx.Root, "_outside");
        Directory.CreateDirectory(outside);
        var outsideYaml = Path.Combine(outside, "workspace.yaml");
        File.WriteAllText(outsideYaml, FixtureStore.SampleYaml(A));
        File.SetLastWriteTimeUtc(outsideYaml, FixtureStore.Now.UtcDateTime.AddDays(-2));
        var before = File.ReadAllBytes(outsideYaml);

        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{fx.SessionDir(A)}\" \"{outside}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

        var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false));

        Assert.Equal(ErrorKind.Refused, ex.Kind);
        Assert.Contains("link or junction", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(outsideYaml));
    }

    [Fact]
    public void Invalid_utf8_yaml_is_refused_untouched()
    {
        using var fx = new FixtureStore();
        fx.AddSession(A);
        fx.AddTurn(A, 0, "u", "a", "2026-01-05T10:05:00.000Z");
        var bad = new byte[] { 0x6E, 0x61, 0x6D, 0x65, 0x3A, 0x20, 0xFF, 0x0A };
        fx.WriteYaml(A, bad);

        Assert.Equal(ErrorKind.Refused, Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false)).Kind);
        Assert.Equal(bad, File.ReadAllBytes(fx.YamlPath(A)));
    }

    [Fact]
    public void Rename_against_wrong_schema_version_refuses_to_write()
    {
        using var fx = new FixtureStore(schemaVersion: 9);
        fx.WriteYaml(A, FixtureStore.SampleYaml(A));
        var bytes = File.ReadAllBytes(fx.YamlPath(A));

        var ex = Assert.Throws<ToolkitException>(() => Renamer(fx).Rename(A, "X", false, false));

        Assert.Equal(ErrorKind.SchemaMismatch, ex.Kind);
        Assert.Contains("9", ex.Message);
        Assert.Equal(bytes, File.ReadAllBytes(fx.YamlPath(A)));
    }
}
