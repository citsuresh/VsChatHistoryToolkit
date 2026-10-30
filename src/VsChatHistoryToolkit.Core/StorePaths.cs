using System.Text.RegularExpressions;

namespace VsChatHistoryToolkit;

/// <summary>Locations inside the Copilot CLI store. The root is injectable so tests never touch the real store.</summary>
public sealed partial class StorePaths
{
    public StorePaths(string root) => Root = root;

    public string Root { get; }

    public string DbPath => Path.Combine(Root, "session-store.db");

    public string SessionStateDir => Path.Combine(Root, "session-state");

    public static StorePaths Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "VisualStudio", "CopilotCli"));

    /// <summary>Session ids become folder names, so only a strict id shape is accepted (no path separators or dots).</summary>
    public static void ValidateSessionId(string id)
    {
        if (string.IsNullOrEmpty(id) || !SessionIdRx().IsMatch(id))
            throw new ToolkitException(ErrorKind.Usage, "Session id must be an exact id (letters, digits, '-' or '_', up to 64 characters).");
    }

    public string SessionDir(string id)
    {
        ValidateSessionId(id);
        return Path.Combine(SessionStateDir, id);
    }

    public string WorkspaceYamlPath(string id) => Path.Combine(SessionDir(id), "workspace.yaml");

    public string EventsPath(string id) => Path.Combine(SessionDir(id), "events.jsonl");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex SessionIdRx();
}
