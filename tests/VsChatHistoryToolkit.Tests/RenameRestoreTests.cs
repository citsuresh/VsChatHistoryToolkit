using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Tests;

public class RenameRestoreTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";

    private static SessionRenamer Renamer(FixtureStore fx) =>
        new(fx.Paths, fx.LogDir, () => FixtureStore.Now, busyTimeoutMs: 200);

    private static string Summary(FixtureStore fx)
    {
        using var c = fx.OpenWriter();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT summary FROM sessions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", A);
        return (string)cmd.ExecuteScalar()!;
    }

    private static void Prepare(FixtureStore fx, SessionRenamer renamer, bool undo)
    {
        fx.AddIdleSession(A, summary: "old summary");
        if (undo) renamer.Rename(A, "Existing rename", false, false);
    }

    private static ChangeResult Apply(SessionRenamer renamer, bool undo) =>
        undo ? renamer.Undo(A, false, false) : renamer.Rename(A, "Target title", false, false);

    private static string[] LogStatuses(FixtureStore fx) => File.ReadAllLines(fx.LogPath)
        .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("status").GetString()!)
        .ToArray();

    private static void FailCommit(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "COMMIT INVALID";
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Summary_update_failure_rolls_back_without_replacing_yaml(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var before = File.ReadAllBytes(fx.YamlPath(A));
        var summary = Summary(fx);
        fx.Exec("CREATE TRIGGER fail_summary BEFORE UPDATE OF summary ON sessions BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;");

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Locked_database_before_begin_leaves_yaml_and_summary_unchanged(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var before = File.ReadAllBytes(fx.YamlPath(A));
        var summary = Summary(fx);

        using (var blocker = fx.OpenWriter())
        {
            using (var cmd = blocker.CreateCommand()) { cmd.CommandText = "BEGIN IMMEDIATE"; cmd.ExecuteNonQuery(); }
            var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

            Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
            Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
            Assert.Contains("Nothing was changed", ex.Message);
            Assert.Equal(before, File.ReadAllBytes(fx.YamlPath(A)));
            Assert.Equal(summary, Summary(fx));
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_snapshot_after_begin_leaves_the_concurrent_yaml_and_database_unchanged(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var summary = Summary(fx);
        byte[]? concurrentBytes = null;
        renamer.StageHook = stage =>
        {
            if (stage != "after-lock") return;
            File.AppendAllText(fx.YamlPath(A), "external: concurrent edit\n");
            concurrentBytes = File.ReadAllBytes(fx.YamlPath(A));
        };

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.Refused, ex.Kind);
        Assert.Equal(ExitCodes.Refused, ex.ExitCode);
        Assert.Contains("changed while the rename was being prepared", ex.Message);
        Assert.Equal(concurrentBytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exhausted_file_replace_retries_roll_back_and_leave_yaml_unchanged(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var before = File.ReadAllBytes(fx.YamlPath(A));
        var summary = Summary(fx);
        FileStream? held = null;
        renamer.StageHook = stage =>
        {
            if (stage == "before-replace") held = new FileStream(fx.YamlPath(A), FileMode.Open, FileAccess.Read, FileShare.None);
        };

        ToolkitException ex;
        try
        {
            ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("Nothing was changed", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Post_replace_verification_failure_reports_yaml_may_differ_and_rolls_back_database(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var summary = Summary(fx);
        byte[]? replacedBytes = null;
        renamer.FileReplaceHook = (temp, path) =>
        {
            File.Replace(temp, path, null);
            File.AppendAllText(path, "external: changed after replacement\n");
            replacedBytes = File.ReadAllBytes(path);
        };

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("database was rolled back, but workspace.yaml was already replaced", ex.Message);
        Assert.Equal(replacedBytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timestamp_read_failure_after_replace_reports_yaml_may_differ_and_rolls_back_database(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        fx.AddIdleSession(A, summary: "old summary");
        if (undo) renamer.Rename(A, "Existing rename", false, false);
        var before = File.ReadAllBytes(fx.YamlPath(A));
        var summary = Summary(fx);
        byte[]? replacedBytes = null;
        renamer.FileReplaceHook = (temp, path) =>
        {
            File.Replace(temp, path, null);
            replacedBytes = File.ReadAllBytes(path);
        };
        renamer.YamlMtimeTicksHook = _ => throw new IOException("synthetic timestamp read failure");

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("database was rolled back, but workspace.yaml was already replaced", ex.Message);
        Assert.Equal(replacedBytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.NotEqual(before, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_failure_restores_the_exact_previous_yaml_and_rolls_back_summary(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var before = File.ReadAllBytes(fx.YamlPath(A));
        var summary = Summary(fx);
        renamer.CommitHook = FailCommit;

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("workspace.yaml was restored to its previous content", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_failure_preserves_a_concurrent_yaml_edit_instead_of_restoring_over_it(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var summary = Summary(fx);
        byte[]? concurrentBytes = null;
        renamer.CommitHook = conn =>
        {
            File.AppendAllText(fx.YamlPath(A), "external: concurrent commit-time edit\n");
            concurrentBytes = File.ReadAllBytes(fx.YamlPath(A));
            FailCommit(conn);
        };

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("changed by someone else after this rename wrote it, so it was left as it is", ex.Message);
        Assert.Equal(concurrentBytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Commit_failure_reports_when_restoring_yaml_also_fails(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        Prepare(fx, renamer, undo);
        var before = File.ReadAllBytes(fx.YamlPath(A));
        var summary = Summary(fx);
        byte[]? attemptedBytes = null;
        var replacements = 0;
        renamer.FileReplaceHook = (temp, path) =>
        {
            if (++replacements == 1)
            {
                attemptedBytes = File.ReadAllBytes(temp);
                File.Replace(temp, path, null);
                return;
            }
            throw new IOException("synthetic restore failure");
        };
        renamer.CommitHook = FailCommit;

        var ex = Assert.Throws<ToolkitException>(() => Apply(renamer, undo));

        Assert.Equal(ErrorKind.WriteFailed, ex.Kind);
        Assert.Equal(ExitCodes.WriteFailed, ex.ExitCode);
        Assert.Contains("could not be restored", ex.Message);
        Assert.Equal(attemptedBytes, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.NotEqual(before, File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(summary, Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending", "failed" } : new[] { "pending", "failed" }, LogStatuses(fx));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Final_undo_log_failure_keeps_applied_state_and_returns_a_warning(bool undo)
    {
        using var fx = new FixtureStore();
        var renamer = Renamer(fx);
        fx.AddIdleSession(A, summary: "old summary");
        var originalBytes = File.ReadAllBytes(fx.YamlPath(A));
        if (undo) renamer.Rename(A, "Existing rename", false, false);
        FileStream? held = null;
        renamer.StageHook = stage =>
        {
            if (stage == "before-applied-log") held = new FileStream(fx.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        };

        ChangeResult result;
        try
        {
            result = Apply(renamer, undo);
        }
        finally
        {
            held?.Dispose();
        }

        Assert.True(result.Applied);
        Assert.Contains("final undo-log entry could not be written", Assert.Single(result.Warnings!));
        Assert.Equal(undo ? originalBytes : Encoding.UTF8.GetBytes(FixtureStore.SampleYaml(A).Replace("name: Original title", "name: Target title").Replace("user_named: false", "user_named: true")), File.ReadAllBytes(fx.YamlPath(A)));
        Assert.Equal(undo ? "old summary" : "Target title", Summary(fx));
        Assert.Equal(undo ? new[] { "pending", "applied", "pending" } : new[] { "pending" }, LogStatuses(fx));
    }
}
