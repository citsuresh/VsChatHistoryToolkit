using System.Globalization;

namespace VsChatHistoryToolkit;

/// <summary>
/// Heuristic for "this session is active or was used recently". Three independent signals, each compared with the
/// window: events.jsonl and workspace.yaml modification times, and the newest turn timestamp in the database.
/// It cannot prove a session is closed, so rename refuses on any hit unless forced.
/// </summary>
public static class ActivityProbe
{
    /// <param name="ownYamlWriteUtc">
    /// Modification time this tool itself gave workspace.yaml on its last write. A file whose time still equals it
    /// has not been touched by anyone else since, so that write is not evidence of activity.
    /// </param>
    public static IReadOnlyList<string> RecentSignals(StorePaths paths, string id, string? lastTurnTimestamp, DateTimeOffset now, TimeSpan window,
        DateTime? ownYamlWriteUtc = null)
    {
        var signals = new List<string>();
        CheckFile(signals, "events.jsonl", paths.EventsPath(id), now, window, null);
        CheckFile(signals, "workspace.yaml", paths.WorkspaceYamlPath(id), now, window, ownYamlWriteUtc);

        if (lastTurnTimestamp is not null
            && DateTimeOffset.TryParse(lastTurnTimestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var last)
            && now - last < window)
        {
            signals.Add($"last turn {Minutes(now - last)} min ago");
        }
        return signals;
    }

    private static void CheckFile(List<string> signals, string label, string path, DateTimeOffset now, TimeSpan window, DateTime? ignoreUtc)
    {
        if (!File.Exists(path)) return;
        var modifiedUtc = File.GetLastWriteTimeUtc(path);
        if (ignoreUtc is { } own && own.Ticks == modifiedUtc.Ticks) return;
        var modified = new DateTimeOffset(modifiedUtc, TimeSpan.Zero);
        if (now - modified < window) signals.Add($"{label} modified {Minutes(now - modified)} min ago");
    }

    private static string Minutes(TimeSpan span) => Math.Max(0, span.TotalMinutes).ToString("0.#", CultureInfo.InvariantCulture);
}
