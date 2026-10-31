using Microsoft.Data.Sqlite;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Cli;

/// <summary>Test seams: where the store lives, where the undo log goes, and what "now" is.</summary>
public sealed record CliEnvironment(string? Root = null, string? LogDir = null, Func<DateTimeOffset>? Clock = null);

public static class CliRunner
{
    public const string Usage = """
        chathistory - search Visual Studio Copilot Chat history (session-store.db only)

        Commands (JSON by default, --text for humans):
          search <terms...> [--limit N]
          sessions [--repo S] [--cwd S] [--branch S] [--from DATE] [--to DATE] [--limit N]
          show <id>
          turns <id> [--index I | --range A-B] [--max-chars N]
          checkpoints <id> [--full]
          usage <id> [--by-turn]
          usage-top [--from DATE] [--to DATE] [--by requests|tokens|aiu] [--limit N]
          files <id> [--limit N]
          files --path <text> [--limit N]
          stats
          doctor
          rename <id> "<title>" [--force] [--dry-run]
          undo-rename <id> [--force] [--dry-run]

        Global: --text, --root <store folder>, --log-dir <undo log folder>, --help
        Exit codes: 0 ok, 1 usage, 2 not found, 3 store unavailable or schema mismatch, 4 refused, 5 write failed, 6 internal
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CliEnvironment? env = null)
    {
        ParsedArgs? parsed = null;
        try
        {
            parsed = ParsedArgs.Parse(args);
            if (parsed.Flag("help") || parsed.Command is null || parsed.Command == "help")
            {
                stdout.WriteLine(Usage);
                return parsed.Command is null && !parsed.Flag("help") ? ExitCodes.Usage : ExitCodes.Ok;
            }

            var result = Dispatch(parsed, env ?? new CliEnvironment());
            stdout.WriteLine(parsed.Flag("text") ? TextFormatter.Format(result) : JsonOut.Serialize(result));
            return ExitCodes.Ok;
        }
        catch (ToolkitException ex)
        {
            return Fail(parsed, stdout, stderr, ex.Code, ex.Message, ex.ExitCode);
        }
        catch (SqliteException ex)
        {
            return Fail(parsed, stdout, stderr, "store_unavailable", $"Database error: {ex.Message}", ExitCodes.StoreUnavailable);
        }
        catch (Exception ex)
        {
            return Fail(parsed, stdout, stderr, "internal", $"Unexpected error: {ex.GetType().Name}: {ex.Message}", ExitCodes.Internal);
        }
    }

    private static int Fail(ParsedArgs? parsed, TextWriter stdout, TextWriter stderr, string code, string message, int exit)
    {
        if (parsed?.Flag("text") == true)
        {
            // Messages can echo user-supplied paths or ids, so they get the same cleaning as normal text output.
            stderr.WriteLine(TextFormatter.Clean($"error ({code}): {message}"));
            stderr.WriteLine($"coverage: {Caps.Coverage}");
        }
        else
        {
            stdout.WriteLine(JsonOut.Error(code, message));
        }
        return exit;
    }

    private static ToolResult Dispatch(ParsedArgs a, CliEnvironment env)
    {
        var root = a.Option("root") ?? env.Root ?? Environment.GetEnvironmentVariable("VSCHATHISTORY_ROOT");
        var paths = root is null ? StorePaths.Default() : new StorePaths(root);
        var store = new ChatStore(paths);

        switch (a.Command)
        {
            case "search":
                if (a.Positionals.Count == 0) throw new ToolkitException(ErrorKind.Usage, "Usage: search <terms...> [--limit N]");
                return store.Search(string.Join(' ', a.Positionals), a.IntOption("limit"));

            case "sessions":
                a.Require(0, "sessions [--repo] [--cwd] [--branch] [--from] [--to] [--limit N]");
                return store.Sessions(new ChatStore.SessionQuery(
                    a.Option("repo"), a.Option("cwd"), a.Option("branch"), a.Option("from"), a.Option("to"), a.IntOption("limit")));

            case "show":
                return store.Show(a.Require(1, "show <id>")[0]);

            case "turns":
                return store.Turns(a.Require(1, "turns <id> [--index i | --range a-b] [--max-chars N]")[0],
                    a.IntOption("index"), a.RangeOption(), a.IntOption("max-chars"));

            case "checkpoints":
                return store.Checkpoints(a.Require(1, "checkpoints <id> [--full]")[0], a.Flag("full"));

            case "usage":
                return store.Usage(a.Require(1, "usage <id> [--by-turn]")[0], a.Flag("by-turn"));

            case "usage-top":
                a.Require(0, "usage-top [--from DATE] [--to DATE] [--by requests|tokens|aiu] [--limit N]");
                return store.UsageTop(a.Option("from"), a.Option("to"), a.Option("by"), a.IntOption("limit"));

            case "doctor":
                a.Require(0, "doctor");
                return store.Doctor();

            case "files":
            {
                const string filesUsage = "files <id> [--limit N]  or  files --path <text> [--limit N]";
                var path = a.Option("path");
                if (path is null) return store.Files(a.Require(1, filesUsage)[0], a.IntOption("limit"));
                a.Require(0, filesUsage);
                return store.FilesByPath(path, a.IntOption("limit"));
            }

            case "stats":
                a.Require(0, "stats");
                return store.Stats();

            case "rename":
            {
                var p = a.Require(2, "rename <id> \"<title>\" [--force] [--dry-run]");
                return Renamer(paths, a, env).Rename(p[0], p[1], a.Flag("force"), a.Flag("dry-run"));
            }

            case "undo-rename":
            {
                var p = a.Require(1, "undo-rename <id> [--force] [--dry-run]");
                return Renamer(paths, a, env).Undo(p[0], a.Flag("force"), a.Flag("dry-run"));
            }

            default:
                throw new ToolkitException(ErrorKind.Usage, $"Unknown command '{a.Command}'. Run 'chathistory --help'.");
        }
    }

    private static SessionRenamer Renamer(StorePaths paths, ParsedArgs a, CliEnvironment env) =>
        new(paths,
            a.Option("log-dir") ?? env.LogDir ?? Environment.GetEnvironmentVariable("VSCHATHISTORY_LOGDIR") ?? SessionRenamer.DefaultLogDir(),
            env.Clock);
}
