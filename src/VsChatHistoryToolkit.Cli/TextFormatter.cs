using System.Text;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Cli;

/// <summary>Human-readable rendering for --text. Same data and caps as the JSON.</summary>
public static class TextFormatter
{
    public static string Format(ToolResult result)
    {
        var sb = new StringBuilder();
        switch (result)
        {
            case SearchResult s:
                sb.AppendLine($"search: {s.Count} hit(s) for [{string.Join(", ", s.Terms)}]");
                foreach (var h in s.Hits)
                    sb.AppendLine($"  {h.SessionId ?? "(unresolved)"}  {h.SourceType}  {h.Snippet}");
                break;
            case SessionsResult s:
                sb.AppendLine($"sessions: {s.Count}");
                foreach (var x in s.Sessions)
                    sb.AppendLine($"  {x.Id}  {x.End ?? x.Start ?? "?"}  {x.TurnCount} turns  {x.Repository ?? "-"}  {x.Branch ?? "-"}  {x.Title ?? "(no title)"}");
                break;
            case ShowResult s:
                sb.AppendLine($"session {s.Session.Id}");
                sb.AppendLine($"  title: {s.Session.Title ?? "(none)"} [{s.Session.TitleSource}]");
                sb.AppendLine($"  repository: {s.Session.Repository ?? "-"}   branch: {s.Session.Branch ?? "-"}");
                sb.AppendLine($"  cwd: {s.Session.Cwd ?? "-"}");
                sb.AppendLine($"  start: {s.Session.Start ?? "-"}   end (last turn): {s.Session.End ?? "-"}   turns: {s.Session.TurnCount}");
                sb.AppendLine($"  solution: {s.Solution}");
                sb.AppendLine($"  user_named: {(s.UserNamed is null ? "-" : s.UserNamed.ToString()!.ToLowerInvariant())}   workspace.yaml: {(s.WorkspaceYamlPresent ? "present" : "missing")}");
                foreach (var c in s.Checkpoints) sb.AppendLine($"  checkpoint {c.Number}: {c.Title ?? "(untitled)"}");
                break;
            case TurnsResult s:
                sb.AppendLine($"turns: {s.Count} of {s.TotalTurns} for {s.SessionId}");
                foreach (var t in s.Turns)
                {
                    sb.AppendLine($"  [{t.Index}] {t.Timestamp}{(t.Truncated ? "  (truncated)" : "")}");
                    sb.AppendLine($"    user: {t.UserMessage}");
                    sb.AppendLine($"    assistant: {t.AssistantResponse}");
                }
                break;
            case CheckpointsResult s:
                sb.AppendLine($"checkpoints: {s.Count} for {s.SessionId}");
                foreach (var c in s.Checkpoints)
                {
                    sb.AppendLine($"  [{c.Number}] {c.Title}");
                    if (c.Overview is not null) sb.AppendLine($"    overview: {c.Overview}");
                    if (c.History is not null) sb.AppendLine($"    history: {c.History}");
                    if (c.WorkDone is not null) sb.AppendLine($"    work_done: {c.WorkDone}");
                    if (c.TechnicalDetails is not null) sb.AppendLine($"    technical_details: {c.TechnicalDetails}");
                    if (c.ImportantFiles is not null) sb.AppendLine($"    important_files: {c.ImportantFiles}");
                    if (c.NextSteps is not null) sb.AppendLine($"    next_steps: {c.NextSteps}");
                }
                break;
            case StatsResult s:
                sb.AppendLine($"stats: {s.TotalSessions} sessions, {s.TotalTurns} turns");
                AppendCounts(sb, "by repository", s.ByRepository);
                AppendCounts(sb, "by cwd", s.ByCwd);
                AppendCounts(sb, "by month", s.ByMonth);
                break;
            case ChangeResult s:
                sb.AppendLine($"{s.Command}{(s.DryRun ? " (dry run)" : "")}: session {s.SessionId}");
                sb.AppendLine($"  title: {s.OldTitle ?? "(none)"}  ->  {s.NewTitle ?? "(none)"}");
                sb.AppendLine($"  name line:       {s.Before.NameLine ?? "(absent)"}  ->  {s.After.NameLine ?? "(absent)"}");
                sb.AppendLine($"  user_named line: {s.Before.UserNamedLine ?? "(absent)"}  ->  {s.After.UserNamedLine ?? "(absent)"}");
                sb.AppendLine($"  summary:         {Short(s.Before.Summary)}  ->  {Short(s.After.Summary)}");
                if (s.Forced) sb.AppendLine($"  forced past: {string.Join("; ", s.ActiveSignals)}");
                sb.AppendLine(s.Applied ? "  applied." : "  NOT applied.");
                foreach (var w in s.Warnings ?? Array.Empty<string>()) sb.AppendLine($"  WARNING: {w}");
                break;
        }

        sb.AppendLine($"coverage: {result.Coverage}{(result.Truncated ? "   (output truncated)" : "")}");
        if (result is ChangeResult change) sb.AppendLine(change.Note);
        return sb.ToString().TrimEnd();
    }

    private static string Short(string? s) =>
        s is null ? "(null)" : "\"" + TextUtilPublic.Cap(s, 60) + "\"";

    private static void AppendCounts(StringBuilder sb, string label, IReadOnlyList<CountItem> items)
    {
        sb.AppendLine($"  {label}:");
        foreach (var i in items) sb.AppendLine($"    {i.Sessions,5}  {i.Key}");
    }
}

internal static class TextUtilPublic
{
    public static string Cap(string s, int max) => s.Length <= max ? s.Replace('\n', ' ') : s[..max].Replace('\n', ' ') + "…";
}
