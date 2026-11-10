namespace VsChatHistoryToolkit;

/// <summary>Hard limits applied to every command. Callers can lower them, never raise them.</summary>
public static class Caps
{
    public const string Coverage = "session-store.db only";

    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public const int MaxSearchTerms = 10;
    public const int MaxTermLength = 64;
    public const int SnippetChars = 200;
    public const int TitleChars = 160;

    public const int DefaultTurnChars = 1500;
    public const int MaxTurnChars = 4000;
    public const int MaxTurnsPerCall = 10;
    public const int TurnsBudgetChars = 40_000;

    public const int MaxCheckpoints = 50;
    public const int CheckpointOverviewChars = 600;
    public const int CheckpointFieldChars = 2000;
    public const int CheckpointsBudgetChars = 60_000;

    public const int StatsTop = 25;

    public const int MaxUsageTurns = 100;
    public const int UsageBreakdownDefaultLimit = 100;
    public const int UsageBreakdownMaxLimit = 500;

    public const int FilePathChars = 400;

    public const int DoctorLargestFiles = 10;

    public const int EventsMaxBytes = 128 * 1024 * 1024;   // the largest real log seen was 64 MB; session-end events sit at the end of a file
    public const int EventsMaxLines = 500_000;
    public const int EventsMaxLineBytes = 1_000_000;
    public const int EventsTop = 25;
    public const int EventsCompactions = 25;
    public const int EventsShutdowns = 10;

    public const int RollupMaxSeconds = 60;
    public const long RollupMaxBytes = 512L * 1024 * 1024;
    public const int RollupTop = 25;
    public const int RollupTopChats = 5;

    public const int RenameTitleMaxLength = 120;
    public const int ActiveWindowMinutes = 10;

    public static int ClampLimit(int? requested, out bool clamped)
    {
        clamped = false;
        if (requested is null) return DefaultLimit;
        if (requested < 1) throw new ToolkitException(ErrorKind.Usage, "--limit must be at least 1.");
        if (requested > MaxLimit) { clamped = true; return MaxLimit; }
        return requested.Value;
    }
}
