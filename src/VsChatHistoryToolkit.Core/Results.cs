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

public sealed record StatsResult(
    int TotalSessions,
    int TotalTurns,
    IReadOnlyList<CountItem> ByRepository,
    IReadOnlyList<CountItem> ByCwd,
    IReadOnlyList<CountItem> ByMonth) : ToolResult("stats");

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
