namespace VsChatHistoryToolkit;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int NotFound = 2;
    public const int StoreUnavailable = 3;
    public const int Refused = 4;
    public const int WriteFailed = 5;
    public const int Internal = 6;
}

public enum ErrorKind
{
    Usage,
    NotFound,
    StoreUnavailable,
    SchemaMismatch,
    Refused,
    WriteFailed,
}

/// <summary>An expected failure with a stable error code and exit code.</summary>
public sealed class ToolkitException : Exception
{
    public ToolkitException(ErrorKind kind, string message) : base(message) => Kind = kind;

    public ErrorKind Kind { get; }

    public int ExitCode => Kind switch
    {
        ErrorKind.Usage => ExitCodes.Usage,
        ErrorKind.NotFound => ExitCodes.NotFound,
        ErrorKind.StoreUnavailable => ExitCodes.StoreUnavailable,
        ErrorKind.SchemaMismatch => ExitCodes.StoreUnavailable,
        ErrorKind.Refused => ExitCodes.Refused,
        ErrorKind.WriteFailed => ExitCodes.WriteFailed,
        _ => ExitCodes.Internal,
    };

    public string Code => Kind switch
    {
        ErrorKind.Usage => "usage",
        ErrorKind.NotFound => "not_found",
        ErrorKind.StoreUnavailable => "store_unavailable",
        ErrorKind.SchemaMismatch => "schema_mismatch",
        ErrorKind.Refused => "refused",
        ErrorKind.WriteFailed => "write_failed",
        _ => "internal",
    };
}
