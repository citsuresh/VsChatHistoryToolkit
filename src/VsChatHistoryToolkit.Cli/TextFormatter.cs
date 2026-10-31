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
                Line(sb,$"search: {s.Count} hit(s) for [{string.Join(", ", s.Terms)}]");
                foreach (var h in s.Hits)
                    Line(sb,$"  {h.SessionId ?? "(unresolved)"}  {h.SourceType}  {h.Snippet}");
                break;
            case SessionsResult s:
                Line(sb,$"sessions: {s.Count}");
                foreach (var x in s.Sessions)
                    Line(sb,$"  {x.Id}  {x.End ?? x.Start ?? "?"}  {x.TurnCount} turns  {x.Repository ?? "-"}  {x.Branch ?? "-"}  {x.Title ?? "(no title)"}");
                break;
            case ShowResult s:
                Line(sb,$"session {s.Session.Id}");
                Line(sb,$"  title: {s.Session.Title ?? "(none)"} [{s.Session.TitleSource}]");
                Line(sb,$"  repository: {s.Session.Repository ?? "-"}   branch: {s.Session.Branch ?? "-"}");
                Line(sb,$"  cwd: {s.Session.Cwd ?? "-"}");
                Line(sb,$"  start: {s.Session.Start ?? "-"}   end (last turn): {s.Session.End ?? "-"}   turns: {s.Session.TurnCount}");
                Line(sb,$"  solution: {s.Solution}");
                Line(sb,$"  user_named: {(s.UserNamed is null ? "-" : s.UserNamed.ToString()!.ToLowerInvariant())}   workspace.yaml: {(s.WorkspaceYamlPresent ? "present" : "missing")}");
                foreach (var c in s.Checkpoints) Line(sb,$"  checkpoint {c.Number}: {c.Title ?? "(untitled)"}");
                break;
            case TurnsResult s:
                Line(sb,$"turns: {s.Count} of {s.TotalTurns} for {s.SessionId}");
                foreach (var t in s.Turns)
                {
                    Line(sb,$"  [{t.Index}] {t.Timestamp}{(t.Truncated ? "  (truncated)" : "")}");
                    Line(sb,$"    user: {t.UserMessage}");
                    Line(sb,$"    assistant: {t.AssistantResponse}");
                }
                break;
            case CheckpointsResult s:
                Line(sb,$"checkpoints: {s.Count} for {s.SessionId}");
                foreach (var c in s.Checkpoints)
                {
                    Line(sb,$"  [{c.Number}] {c.Title}");
                    if (c.Overview is not null) Line(sb,$"    overview: {c.Overview}");
                    if (c.History is not null) Line(sb,$"    history: {c.History}");
                    if (c.WorkDone is not null) Line(sb,$"    work_done: {c.WorkDone}");
                    if (c.TechnicalDetails is not null) Line(sb,$"    technical_details: {c.TechnicalDetails}");
                    if (c.ImportantFiles is not null) Line(sb,$"    important_files: {c.ImportantFiles}");
                    if (c.NextSteps is not null) Line(sb,$"    next_steps: {c.NextSteps}");
                }
                break;
            case StatsResult s:
                Line(sb,$"stats: {s.TotalSessions} sessions, {s.TotalTurns} turns");
                AppendCounts(sb, "by repository", s.ByRepository);
                AppendCounts(sb, "by cwd", s.ByCwd);
                AppendCounts(sb, "by month", s.ByMonth);
                if (s.Usage is { } u)
                {
                    Line(sb,$"  usage: {u.SessionsWithUsage} session(s) with usage rows");
                    Line(sb,$"    total: {UsageLine(u.Total)}");
                    foreach (var m in u.ByModel) Line(sb,$"    model {m.Model}: {UsageLine(m.Totals)}");
                    foreach (var m in u.ByMonth) Line(sb,$"    month {m.Month}: {UsageLine(m.Totals)}");
                }
                break;
            case UsageResult s:
                Line(sb,$"usage: {s.SessionId}");
                if (!s.Recorded) { Line(sb,$"  {s.Note}"); break; }
                Line(sb,$"  first request: {s.FirstAt ?? "-"}   last request: {s.LastAt ?? "-"}");
                Line(sb,$"  total:     {UsageLine(s.Total!)}");
                Line(sb,$"  main:      {UsageLine(s.Main!)}");
                Line(sb,$"  subagents: {UsageLine(s.Subagents!)}");
                Line(sb,$"  main prompt size: peak {Num(s.PeakInputTokens)}, last {Num(s.LastInputTokens)} input tokens");
                foreach (var m in s.ByModel) Line(sb,$"  model {m.Model}: {UsageLine(m.Totals)}");
                foreach (var t in s.ByTurn ?? Array.Empty<UsageTurnItem>()) Line(sb,$"  turn {t.TurnIndex}: {UsageLine(t.Totals)}, main peak in {Num(t.PeakInputTokens)}");
                Line(sb,"  (nano_aiu is the stored cost unit; input tokens count the re-sent context on every request)");
                if (s.Total!.Credits is not null) Line(sb, CreditsNote);
                break;
            case UsageTopResult s:
                Line(sb,$"usage-top: {s.Count} chat(s) by {s.By}{(s.From is null ? "" : $" from {s.From}")}{(s.To is null ? "" : $" before {s.To}")}");
                if (!s.Recorded) { Line(sb,$"  {s.Note}"); break; }
                if (s.Note is not null) Line(sb,$"  {s.Note}");
                foreach (var x in s.Sessions)
                    Line(sb,$"  {x.SessionId}  {x.LastAt ?? "?"}  {x.Requests} requests, in {x.InputTokens} (cache read {x.CacheReadTokens}), out {x.OutputTokens}, reasoning {x.ReasoningTokens}, nano_aiu {x.NanoAiu}{Credits(x.Credits)}  {x.Title ?? "(no title)"}");
                Line(sb,"  (nano_aiu is the stored cost unit; input tokens count the re-sent context on every request)");
                if (s.Sessions.Any(x => x.Credits is not null)) Line(sb, CreditsNote);
                break;
            case DoctorResult s:
                Line(sb,"doctor (read-only)");
                Line(sb,$"  session rows: {s.SessionRows}");
                if (!s.SessionStateListed)
                    Line(sb,"  session-state could not be listed: folder counts are unknown");
                Line(sb,$"  session folders: {Num(s.SessionFolders)}{(s.SessionStateFolderPresent ? "" : "  (session-state folder missing)")}");
                Line(sb,$"  rows without a folder: {Num(s.RowsWithoutFolder)}");
                foreach (var id in s.RowsWithoutFolderIds) Line(sb,$"    {id}");
                Line(sb,$"  folders without a row: {Num(s.FoldersWithoutRow)}");
                foreach (var id in s.FoldersWithoutRowNames) Line(sb,$"    {id}");
                Line(sb,$"  sessions without turns: {s.SessionsWithoutTurns}");
                if (s.SessionsWithoutUsage is { } nu) Line(sb,$"  sessions without usage rows: {nu}");
                Line(sb,"  largest events.jsonl:");
                foreach (var e in s.LargestEventsFiles) Line(sb,$"    {e.Bytes,12} bytes  {e.SessionId}");
                break;
            case EventsResult s:
                Line(sb,$"events: {s.SessionId} ({(s.Present ? "present" : "missing")})");
                if (!s.Present) Line(sb,$"  {s.Note}");
                Line(sb,$"  scan: complete {s.Scan.Complete}, {s.Scan.BytesScanned} bytes, {s.Scan.LinesScanned} lines");
                AppendEventCounts(sb, "event types", s.EventCounts.Select(x => new EventNameCount(x.Type, x.Count)));
                AppendEventCounts(sb, "tools", s.Tools);
                AppendEventCounts(sb, "permission kinds", s.PermissionKinds);
                AppendEventCounts(sb, "skills", s.Skills);
                Line(sb,$"  aborts: {s.Aborts}; subagents started: {s.SubagentsStarted}");
                Line(sb,$"  compactions: {s.CompactionCount}");
                foreach (var c in s.Compactions)
                    Line(sb,$"    checkpoint {Num(c.CheckpointNumber)}  {c.Trigger ?? "-"}  success {c.Success?.ToString().ToLowerInvariant() ?? "-"}  tokens {Num(c.PreCompactionTokens)} -> {Num(c.PostCompactionTokens)} (removed {Num(c.TokensRemoved)})  messages removed {Num(c.MessagesRemoved)}  limit {Num(c.TokenLimit)}");
                foreach (var shutdown in s.Shutdowns)
                {
                    Line(sb,$"  shutdown: {shutdown.ShutdownType ?? "-"}  premium requests {Num(shutdown.TotalPremiumRequests)}  nano_aiu {Num(shutdown.TotalNanoAiu)}  changes +{Num(shutdown.LinesAdded)}/-{Num(shutdown.LinesRemoved)} across {Num(shutdown.FilesModified)} files  API {Num(shutdown.TotalApiDurationMs)} ms");
                    Line(sb,$"    tokens: current {Num(shutdown.CurrentTokens)}, system {Num(shutdown.SystemTokens)}, conversation {Num(shutdown.ConversationTokens)}, tool definitions {Num(shutdown.ToolDefinitionsTokens)}");
                    foreach (var model in shutdown.Models ?? Array.Empty<EventModelMetric>())
                        Line(sb,$"    model {model.Model ?? "-"}: {Num(model.Requests?.Count)} requests, cost {model.Requests?.Cost?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
                }
                break;
            case ActivityResult s:
                Line(sb,$"activity: {s.Count} day(s){(s.From is null ? "" : $" from {s.From}")}{(s.To is null ? "" : $" before {s.To}")}");
                foreach (var d in s.Days)
                    Line(sb,$"  {d.Day}: {d.Turns} turns, {d.Chats} chats{(d.Requests is null ? "" : $", {d.Requests} requests, nano_aiu {Num(d.NanoAiu)}{Credits(d.Credits)}")}");
                break;
            case FilesResult s:
                Line(sb,$"files: {s.Count} for {s.SessionId}");
                if (!s.Recorded) { Line(sb,$"  {s.Note}"); break; }
                foreach (var f in s.Files)
                    Line(sb,$"  turn {Num(f.TurnIndex)}  {f.ToolName ?? "-"}  {f.FirstSeenAt ?? "-"}  {f.FilePath}");
                break;
            case FilesByPathResult s:
                Line(sb,$"files: {s.Count} chat(s) touched a path containing \"{s.Path}\"");
                if (!s.Recorded) { Line(sb,$"  {s.Note}"); break; }
                foreach (var x in s.Sessions)
                    Line(sb,$"  {x.SessionId}  {x.LastSeenAt ?? "?"}  {x.FileCount} file(s)  last turn {Num(x.LastTurn)}  {x.Title ?? "(no title)"}");
                break;
            case ChangeResult s:
                Line(sb,$"{s.Command}{(s.DryRun ? " (dry run)" : "")}: session {s.SessionId}");
                Line(sb,$"  title: {s.OldTitle ?? "(none)"}  ->  {s.NewTitle ?? "(none)"}");
                Line(sb,$"  name line:       {s.Before.NameLine ?? "(absent)"}  ->  {s.After.NameLine ?? "(absent)"}");
                Line(sb,$"  user_named line: {s.Before.UserNamedLine ?? "(absent)"}  ->  {s.After.UserNamedLine ?? "(absent)"}");
                Line(sb,$"  summary:         {Short(s.Before.Summary)}  ->  {Short(s.After.Summary)}");
                if (s.Forced) Line(sb,$"  forced past: {string.Join("; ", s.ActiveSignals)}");
                Line(sb,s.Applied ? "  applied." : "  NOT applied.");
                foreach (var w in s.Warnings ?? Array.Empty<string>()) Line(sb,$"  WARNING: {w}");
                break;
        }

        Line(sb,$"coverage: {result.Coverage}{(result.Truncated ? "   (output truncated)" : "")}");
        if (result is ChangeResult change) Line(sb,change.Note);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Adds one output line. Every string printed here can contain chat text, so the whole line is cleaned: a
    /// stored escape sequence must not move the cursor, retitle the window or hide text, and an embedded line break
    /// must not fake a new line. Line breaks and tabs become spaces; other control characters are shown as a
    /// visible symbol.
    /// </summary>
    private static void Line(StringBuilder sb, string line) => sb.AppendLine(Clean(line));

    internal static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\r' or '\n' or '\t' || ch == (char)0x2028 || ch == (char)0x2029) sb.Append(' ');
            else if (ch < 0x20) sb.Append((char)(0x2400 + ch));
            else if (ch == 0x7F) sb.Append((char)0x2421);
            else if (ch >= 0x80 && ch <= 0x9F) sb.Append('?');
            else if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) sb.Append('?');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string UsageLine(UsageTotals t) =>
        $"{t.Requests} requests, in {t.InputTokens} (cache read {t.CacheReadTokens}, cache write {t.CacheWriteTokens}), out {t.OutputTokens}, reasoning {t.ReasoningTokens}, nano_aiu {t.NanoAiu}{Credits(t.Credits)}, {t.DurationMs} ms";

    /// <summary>", credits N" with at least one decimal (11.0, 0.25), or nothing when the store has no request_multiplier column.</summary>
    private static string Credits(double? c) =>
        c is { } v ? ", credits " + v.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) : "";

    private const string CreditsNote =
        "  (credits sum request_multiplier; the main-chat figure matched the chat footers, subagent credits are unverified)";

    private static string Num(long? n) => n?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string Short(string? s) =>
        s is null ? "(null)" : "\"" + TextUtilPublic.Cap(s, 60) + "\"";

    private static void AppendCounts(StringBuilder sb, string label, IReadOnlyList<CountItem> items)
    {
        Line(sb,$"  {label}:");
        foreach (var i in items) Line(sb,$"    {i.Sessions,5}  {i.Key}");
    }

    private static void AppendEventCounts(StringBuilder sb, string label, IEnumerable<EventNameCount> items)
    {
        Line(sb,$"  {label}:");
        foreach (var item in items) Line(sb,$"    {item.Count,5}  {item.Name}");
    }
}

internal static class TextUtilPublic
{
    public static string Cap(string s, int max) => s.Length <= max ? s.Replace('\n', ' ') : s[..max].Replace('\n', ' ') + "…";
}
