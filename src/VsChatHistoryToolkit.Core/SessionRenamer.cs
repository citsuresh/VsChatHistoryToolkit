using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VsChatHistoryToolkit;

/// <summary>
/// The only writer. A rename changes exactly two things for exactly one session: the "name:"/"user_named:" lines in
/// workspace.yaml and sessions.summary. Order of operations (so a common failure leaves nothing half-done):
/// validate, log intent, take the database write lock, update the row, atomically replace the YAML, commit.
/// </summary>
public sealed class SessionRenamer
{
    public const string RestartNote = "New title appears after Visual Studio restarts.";

    private const int ReplaceAttempts = 5;

    private sealed record LogEntry(
        string OpId, string At, string Kind, string SessionId, string Status,
        SessionState Before, SessionState After, string? Undoes, bool Forced, string? Error, long? YamlMtimeTicks = null);

    private readonly StorePaths _paths;
    private readonly string _logDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _busyTimeoutMs;
    private readonly TimeSpan _window;

    public SessionRenamer(StorePaths paths, string logDir, Func<DateTimeOffset>? clock = null,
        int busyTimeoutMs = 10_000, TimeSpan? activeWindow = null)
    {
        _paths = paths;
        _logDir = logDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _busyTimeoutMs = busyTimeoutMs;
        _window = activeWindow ?? TimeSpan.FromMinutes(Caps.ActiveWindowMinutes);
    }

    public static string DefaultLogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VsChatHistoryToolkit");

    public string LogPath => Path.Combine(_logDir, "rename-log.jsonl");

    /// <summary>Test seam: called with a stage name ("after-lock", "before-applied-log") at fixed points of a write.</summary>
    internal Action<string>? StageHook { get; set; }

    public ChangeResult Rename(string id, string rawTitle, bool force, bool dryRun)
    {
        var title = TitleRules.Normalize(rawTitle);
        var target = new SessionState(
            "name: " + TitleRules.FormatYamlValue(title),
            "user_named: true",
            title);
        return Apply("rename", id, force, dryRun, _ => target, undoes: null);
    }

    public ChangeResult Undo(string id, bool force, bool dryRun)
    {
        StorePaths.ValidateSessionId(id);
        var entries = ReadLog();
        var undone = entries.Where(e => e.Kind == "undo-rename" && e.Status == "applied" && e.Undoes is not null)
            .Select(e => e.Undoes!).ToHashSet();
        var last = entries.LastOrDefault(e => e.Kind == "rename" && e.Status == "applied"
            && e.SessionId == id && !undone.Contains(e.OpId));
        if (last is null)
            throw new ToolkitException(ErrorKind.NotFound, $"No applied rename for session '{id}' is waiting to be undone in the undo log.");

        return Apply("undo-rename", id, force, dryRun, current =>
        {
            if (!force && current != last.After)
                throw new ToolkitException(ErrorKind.Refused,
                    "The title, user_named line or summary changed after the logged rename (Visual Studio or a person edited it). Use --force to restore the logged old values anyway.");
            return last.Before;
        }, undoes: last.OpId);
    }

    private ChangeResult Apply(string kind, string id, bool force, bool dryRun, Func<SessionState, SessionState> targetFor, string? undoes)
    {
        StorePaths.ValidateSessionId(id);

        var dir = _paths.SessionDir(id);
        if (!Directory.Exists(dir))
            throw new ToolkitException(ErrorKind.NotFound, $"No session-state folder for session '{id}'.");
        var yamlPath = _paths.WorkspaceYamlPath(id);
        if (!File.Exists(yamlPath))
            throw new ToolkitException(ErrorKind.NotFound, $"workspace.yaml is missing for session '{id}'.");

        // A linked folder or file could redirect the write outside the store, so links are refused.
        if (IsLink(_paths.SessionStateDir) || IsLink(dir) || IsLink(yamlPath))
            throw new ToolkitException(ErrorKind.Refused,
                $"The session-state folder, the session folder or workspace.yaml for '{id}' is a symbolic link or junction (or could not be inspected); refusing to write through it.");

        var original = ReadShared(yamlPath);
        var yaml = WorkspaceYaml.Parse(original, strict: true);
        foreach (var key in new[] { "name", "user_named" })
            if (yaml.CountKey(key) > 1)
                throw new ToolkitException(ErrorKind.Refused,
                    $"workspace.yaml has more than one top-level '{key}:' line; refusing to guess which one Visual Studio uses.");

        using var conn = dryRun
            ? StoreConnection.OpenReadOnly(_paths, _busyTimeoutMs)
            : StoreConnection.OpenReadWrite(_paths, _busyTimeoutMs);

        var (summary, lastTurn) = ReadRow(conn, id);
        var now = _clock();
        var ownWrite = ReadLog().LastOrDefault(e => e.SessionId == id && e.Status == "applied" && e.YamlMtimeTicks is not null)?.YamlMtimeTicks;
        var signals = ActivityProbe.RecentSignals(_paths, id, lastTurn, now, _window,
            ownWrite is { } ticks ? new DateTime(ticks, DateTimeKind.Utc) : null);
        if (signals.Count > 0 && !force)
            throw new ToolkitException(ErrorKind.Refused,
                $"Session '{id}' looks active or recently used ({string.Join("; ", signals)}). Use --force to override.");

        var before = new SessionState(yaml.GetLine("name"), yaml.GetLine("user_named"), summary);
        var after = targetFor(before);
        var result = BuildResult(kind, id, dryRun, applied: false, force && signals.Count > 0, signals, before, after);
        if (dryRun) return result;

        var opId = Guid.NewGuid().ToString("N");
        long? yamlTicks = null;
        LogEntry Entry(string status, string? error = null) =>
            new(opId, now.ToString("o", CultureInfo.InvariantCulture), kind, id, status, before, after, undoes, force && signals.Count > 0, error, yamlTicks);

        try
        {
            AppendLog(Entry("pending"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolkitException(ErrorKind.WriteFailed, $"Cannot write the undo log '{LogPath}': {ex.Message}. Nothing was changed.");
        }

        try
        {
            StoreConnection.Exec(conn, "BEGIN IMMEDIATE");
        }
        catch (SqliteException ex)
        {
            Fail(Entry("failed", ex.Message));
            throw new ToolkitException(ErrorKind.WriteFailed,
                $"session-store.db is locked or not writable ({ex.Message}). Nothing was changed.");
        }

        StageHook?.Invoke("after-lock");
        try
        {
            // The snapshot above was read before the lock; make sure nobody changed either place meanwhile.
            var (currentSummary, _) = ReadRow(conn, id);
            if (currentSummary != summary || !SameBytes(yamlPath, original))
                throw new ToolkitException(ErrorKind.Refused,
                    $"The title or summary of session '{id}' changed while the rename was being prepared (another process wrote to it). Nothing was changed; run the command again.");
        }
        catch (Exception ex)
        {
            Rollback(conn);
            Fail(Entry("failed", ex.Message));
            if (ex is ToolkitException) throw;
            throw new ToolkitException(ErrorKind.WriteFailed, $"Rename failed ({ex.Message}). Nothing was changed.");
        }

        byte[]? written = null;
        var yamlReplaced = false;
        try
        {
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE sessions SET summary = $s WHERE id = $id";
            update.Parameters.AddWithValue("$s", (object?)after.Summary ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", id);
            if (update.ExecuteNonQuery() != 1) throw new ToolkitException(ErrorKind.NotFound, $"No database row for session '{id}'.");

            SetOrRemove(yaml, "name", after.NameLine);
            SetOrRemove(yaml, "user_named", after.UserNamedLine);
            written = yaml.ToBytes();
            ReplaceAtomically(yamlPath, written, original);
            yamlReplaced = true;
            if (!SameBytes(yamlPath, written))
                throw new IOException("workspace.yaml was changed by another program while it was being written");
            yamlTicks = File.GetLastWriteTimeUtc(yamlPath).Ticks;
        }
        catch (Exception ex)
        {
            Rollback(conn);
            Fail(Entry("failed", ex.Message));
            if (!yamlReplaced)
            {
                if (ex is ToolkitException) throw;
                throw new ToolkitException(ErrorKind.WriteFailed, $"Rename failed ({ex.Message}). Nothing was changed.");
            }
            throw new ToolkitException(ErrorKind.WriteFailed,
                $"Rename failed ({ex.Message}). The database was rolled back, but workspace.yaml was already replaced and may differ from before; check it by hand.");
        }

        try
        {
            StoreConnection.Exec(conn, "COMMIT");
        }
        catch (SqliteException ex)
        {
            Rollback(conn);
            string state;
            try
            {
                // Only put the old content back if the file is still exactly what this rename wrote;
                // otherwise someone else (Visual Studio or a person) has edited it since, and that edit must survive.
                if (SameBytes(yamlPath, written!))
                {
                    ReplaceAtomically(yamlPath, original, written!);
                    state = "workspace.yaml was restored to its previous content; nothing was changed.";
                }
                else
                {
                    state = "workspace.yaml was changed by someone else after this rename wrote it, so it was left as it is; the database was not changed. Check it by hand.";
                }
            }
            catch (Exception restoreEx)
            {
                state = $"workspace.yaml is already changed and could not be restored ({restoreEx.Message}); the database was not changed. Restore it by hand.";
            }
            Fail(Entry("failed", ex.Message));
            throw new ToolkitException(ErrorKind.WriteFailed, $"Database commit failed ({ex.Message}). {state}");
        }

        StageHook?.Invoke("before-applied-log");
        var logged = TryLog(Entry("applied"));
        return result with
        {
            Applied = true,
            Warnings = logged ? null : new[]
            {
                $"The change is applied, but the final undo-log entry could not be written to '{LogPath}'. "
                + "undo-rename may not find it; the earlier 'pending' entry still holds the old values.",
            },
        };
    }

    private ChangeResult BuildResult(string kind, string id, bool dryRun, bool applied, bool forced,
        IReadOnlyList<string> signals, SessionState before, SessionState after) =>
        new(kind, id, dryRun, applied, forced, signals,
            before.NameLine is null ? null : WorkspaceYaml.ValueOf(before.NameLine, "name"),
            after.NameLine is null ? null : WorkspaceYaml.ValueOf(after.NameLine, "name"),
            before, after, RestartNote);

    private static void SetOrRemove(WorkspaceYaml yaml, string key, string? line)
    {
        if (line is null) yaml.RemoveLine(key);
        else yaml.SetLine(key, line);
    }

    private static (string? Summary, string? LastTurn) ReadRow(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT summary, (SELECT MAX(timestamp) FROM turns WHERE session_id = $id) FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ToolkitException(ErrorKind.NotFound, $"No database row for session '{id}'.");
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Writes a temp file next to the target, swaps it in, and checks the bytes that landed.</summary>
    /// <param name="expectedBefore">What the file must still contain; checked again before every retry so a later
    /// attempt never overwrites an edit made while the file was held.</param>
    private void ReplaceAtomically(string path, byte[] bytes, byte[] expectedBefore)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            // Antivirus, indexers or Visual Studio can hold the file for a moment; retry briefly before giving up.
            for (var attempt = 1; ; attempt++)
            {
                StageHook?.Invoke(attempt == 1 ? "before-replace" : "before-replace-retry");
                try
                {
                    // A retry only goes ahead after the current bytes were read and still match; a failed read
                    // (the file may still be held) counts as a failed attempt, never as permission to replace.
                    if (attempt > 1 && !SameBytes(path, expectedBefore))
                        throw new ToolkitException(ErrorKind.Refused,
                            "workspace.yaml was changed by another program while the replace was being retried. Nothing was changed.");

                    File.Replace(temp, path, null);
                    break;
                }
                catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < ReplaceAttempts)
                {
                    Thread.Sleep(40 * attempt);
                }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static bool SameBytes(string path, byte[] expected) => ReadShared(path).AsSpan().SequenceEqual(expected);

    private static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell, so treat it as a link and refuse
        }
    }

    private static void Rollback(SqliteConnection conn)
    {
        try { StoreConnection.Exec(conn, "ROLLBACK"); } catch (SqliteException) { }
    }

    private void Fail(LogEntry entry) => TryLog(entry);

    private bool TryLog(LogEntry entry)
    {
        try
        {
            AppendLog(entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void AppendLog(LogEntry entry)
    {
        Directory.CreateDirectory(_logDir);
        File.AppendAllText(LogPath, JsonSerializer.Serialize(entry, JsonOut.Options) + "\n");
    }

    private List<LogEntry> ReadLog()
    {
        var list = new List<LogEntry>();
        if (!File.Exists(LogPath)) return list;
        foreach (var line in File.ReadLines(LogPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<LogEntry>(line, JsonOut.Options) is { } e) list.Add(e);
            }
            catch (JsonException) { }
        }
        return list;
    }
}
