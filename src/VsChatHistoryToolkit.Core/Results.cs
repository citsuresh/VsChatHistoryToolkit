namespace VsChatHistoryToolkit;

/// <summary>Common envelope: every successful result says which command ran, its coverage, and whether anything was cut.</summary>
public abstract record ToolResult(string Command)
{
    public bool Ok => true;
    public string Coverage => Caps.Coverage;
    public bool Truncated { get; init; }
}

public sealed record SearchHit(string? SessionId, string SourceType, string SourceId, string Snippet);

public sealed record SearchResult(string Query, IReadOnlyList<string> Terms, int Limit, IReadOnlyList<SearchHit> Hits)
    : ToolResult("search")
{
    public int Count => Hits.Count;
}

public sealed record SessionSummary(
    string Id,
    string? Title,
    string TitleSource,
    string? Repository,
    string? Cwd,
    string? Branch,
    string? Start,
    string? End,
    int TurnCount);

public sealed record SessionsResult(int Limit, IReadOnlyList<SessionSummary> Sessions) : ToolResult("sessions")
{
    public int Count => Sessions.Count;
}

public sealed record CheckpointTitle(long Number, string? Title);

public sealed record ShowResult(
    SessionSummary Session,
    string? HostType,
    string? Summary,
    bool? UserNamed,
    string Solution,
    string SolutionSource,
    bool WorkspaceYamlPresent,
    IReadOnlyList<CheckpointTitle> Checkpoints) : ToolResult("show");

public sealed record TurnItem(
    long Index,
    string? Timestamp,
    string? UserMessage,
    string? AssistantResponse,
    int UserMessageChars,
    int AssistantResponseChars,
    bool Truncated);

public sealed record TurnsResult(string SessionId, int TotalTurns, IReadOnlyList<TurnItem> Turns) : ToolResult("turns")
{
    public int Count => Turns.Count;
}

public sealed record CheckpointItem(
    long Number,
    string? Title,
    string? Overview,
    string? CreatedAt,
    string? History,
    string? WorkDone,
    string? TechnicalDetails,
    string? ImportantFiles,
    string? NextSteps);

public sealed record CheckpointsResult(string SessionId, bool Full, IReadOnlyList<CheckpointItem> Checkpoints)
    : ToolResult("checkpoints")
{
    public int Count => Checkpoints.Count;
}

public sealed record CountItem(string Key, int Sessions);

/// <summary>
/// Sums of the stored per-request usage rows. Values are exactly as stored: input_tokens is the whole prompt of each
/// request (so summing it over a chat counts the re-sent context again every request), and nano_aiu is the stored cost
/// unit, whose relation to the credits shown in the chat window is not verified.
/// </summary>
public sealed record UsageTotals(
    long Requests,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    long NanoAiu,
    long DurationMs);

public sealed record UsageModelItem(string Model, UsageTotals Totals);

/// <summary>PeakInputTokens is the largest prompt of a main-chat request in that turn; null when the turn has only subagent requests.</summary>
public sealed record UsageTurnItem(long TurnIndex, UsageTotals Totals, long? PeakInputTokens);

/// <summary>
/// PeakInputTokens and LastInputTokens describe the prompt size of main-chat requests only (no agent_id): the largest
/// one, and the one of the latest request (by created_at, then rowid). Both are null when there are no main requests.
/// </summary>
public sealed record UsageResult(
    string SessionId,
    bool Recorded,
    string? Note,
    UsageTotals? Total,
    UsageTotals? Main,
    UsageTotals? Subagents,
    string? FirstAt,
    string? LastAt,
    long? PeakInputTokens,
    long? LastInputTokens,
    IReadOnlyList<UsageModelItem> ByModel,
    IReadOnlyList<UsageTurnItem>? ByTurn) : ToolResult("usage");

public sealed record FileItem(string FilePath, string? ToolName, long? TurnIndex, string? FirstSeenAt);

/// <summary>Files edited or created in one chat, from session_files. Recorded is false when the store has no such table or the chat has no rows.</summary>
public sealed record FilesResult(string SessionId, bool Recorded, string? Note, int Limit, IReadOnlyList<FileItem> Files)
    : ToolResult("files")
{
    public int Count => Files.Count;
}

public sealed record FileSessionItem(string SessionId, string? Title, string TitleSource, int FileCount, long? LastTurn, string? LastSeenAt);

/// <summary>Chats that touched a file whose path contains <see cref="Path"/>. Recorded is false only when the store has no usable session_files table.</summary>
public sealed record FilesByPathResult(string Path, bool Recorded, string? Note, int Limit, IReadOnlyList<FileSessionItem> Sessions)
    : ToolResult("files")
{
    public int Count => Sessions.Count;
}

public sealed record UsageMonthItem(string Month, UsageTotals Totals);

public sealed record UsageTopItem(
    string SessionId,
    string? Title,
    string TitleSource,
    long Requests,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long ReasoningTokens,
    long NanoAiu,
    string? LastAt);

/// <summary>Chats ranked by usage. From/To are the normalized UTC bounds that were applied (To is exclusive). Recorded is false when the store has no usable usage table.</summary>
public sealed record UsageTopResult(string By, string? From, string? To, bool Recorded, string? Note, int Limit, IReadOnlyList<UsageTopItem> Sessions)
    : ToolResult("usage-top")
{
    public int Count => Sessions.Count;
}

public sealed record EventsFileItem(string SessionId, long Bytes);

public sealed record EventScan(bool Complete, long BytesScanned, int LinesScanned);
public sealed record EventCountItem(string Type, long Count);
public sealed record EventNameCount(string Name, long Count);
public sealed record EventCompaction(
    long? CheckpointNumber, string? Trigger, bool? Success, long? PreCompactionTokens, long? PostCompactionTokens,
    long? TokensRemoved, long? MessagesRemoved, long? TokenLimit);
public sealed record EventModelRequests(long? Count, decimal? Cost);
public sealed record EventModelMetric(string? Model, EventModelRequests? Requests);
public sealed record EventShutdown(
    string? ShutdownType, long? TotalPremiumRequests, long? TotalNanoAiu, long? LinesAdded, long? LinesRemoved,
    int? FilesModified, long? CurrentTokens, long? SystemTokens, long? ConversationTokens, long? ToolDefinitionsTokens,
    long? TotalApiDurationMs, IReadOnlyList<EventModelMetric>? Models);

/// <summary>A bounded, read-only summary of one session's events.jsonl. Event payload text is never returned.</summary>
public sealed record EventsResult(
    string SessionId, bool Present, string? Note, EventScan Scan, IReadOnlyList<EventCountItem> EventCounts,
    IReadOnlyList<EventNameCount> Tools, IReadOnlyList<EventNameCount> PermissionKinds, IReadOnlyList<EventNameCount> Skills,
    long Aborts, long SubagentsStarted, long CompactionCount, IReadOnlyList<EventCompaction> Compactions,
    IReadOnlyList<EventShutdown> Shutdowns) : ToolResult("events");

public sealed record ActivityDayItem(string Day, long Turns, long Chats, long? Requests, long? NanoAiu);

/// <summary>Daily turn and optional usage counts, ordered newest day first.</summary>
public sealed record ActivityResult(string? From, string? To, int Limit, IReadOnlyList<ActivityDayItem> Days) : ToolResult("activity")
{
    public int Count => Days.Count;
}

/// <summary>
/// Read-only health report. The id lists are samples (ordered, capped like stats); the counts are complete.
/// SessionStateListed is false when session-state exists but could not be listed: the folder counts are then null
/// (unknown) and the folder-based lists are empty. SessionsWithoutUsage is null when the store has no usable usage table.
/// </summary>
public sealed record DoctorResult(
    int SessionRows,
    bool SessionStateFolderPresent,
    bool SessionStateListed,
    int? SessionFolders,
    int? RowsWithoutFolder,
    IReadOnlyList<string> RowsWithoutFolderIds,
    int? FoldersWithoutRow,
    IReadOnlyList<string> FoldersWithoutRowNames,
    int SessionsWithoutTurns,
    int? SessionsWithoutUsage,
    IReadOnlyList<EventsFileItem> LargestEventsFiles) : ToolResult("doctor");

/// <summary>Usage across all sessions; absent from stats when the store has no usage table.</summary>
public sealed record UsageOverview(
    int SessionsWithUsage,
    UsageTotals Total,
    IReadOnlyList<UsageModelItem> ByModel,
    IReadOnlyList<UsageMonthItem> ByMonth);

public sealed record StatsResult(
    int TotalSessions,
    int TotalTurns,
    IReadOnlyList<CountItem> ByRepository,
    IReadOnlyList<CountItem> ByCwd,
    IReadOnlyList<CountItem> ByMonth,
    UsageOverview? Usage = null) : ToolResult("stats");

/// <summary>The three values a rename changes. Null means "absent" (line missing / NULL summary).</summary>
public sealed record SessionState(string? NameLine, string? UserNamedLine, string? Summary);

public sealed record ChangeResult(
    string Command,
    string SessionId,
    bool DryRun,
    bool Applied,
    bool Forced,
    IReadOnlyList<string> ActiveSignals,
    string? OldTitle,
    string? NewTitle,
    SessionState Before,
    SessionState After,
    string Note,
    IReadOnlyList<string>? Warnings = null) : ToolResult(Command);
