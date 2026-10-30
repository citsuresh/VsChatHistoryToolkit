using System.Globalization;
using VsChatHistoryToolkit;

namespace VsChatHistoryToolkit.Cli;

/// <summary>Small argument parser: "--name value" options, boolean flags, positionals, and "--" to end options.</summary>
public sealed class ParsedArgs
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "limit", "repo", "cwd", "branch", "from", "to", "index", "range", "max-chars", "root", "log-dir",
    };

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "text", "force", "dry-run", "full", "help",
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public string? Command { get; private set; }

    public List<string> Positionals { get; } = new();

    public static ParsedArgs Parse(IReadOnlyList<string> args)
    {
        var p = new ParsedArgs();
        var optionsEnded = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!optionsEnded && a == "--") { optionsEnded = true; continue; }

            if (!optionsEnded && a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                string? inline = null;
                var eq = name.IndexOf('=');
                if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }

                if (Flags.Contains(name)) { p._flags.Add(name); continue; }
                if (!ValueOptions.Contains(name))
                    throw new ToolkitException(ErrorKind.Usage, $"Unknown option '--{name}'.");
                if (inline is null)
                {
                    if (i + 1 >= args.Count) throw new ToolkitException(ErrorKind.Usage, $"Option '--{name}' needs a value.");
                    inline = args[++i];
                }
                p._options[name] = inline;
                continue;
            }

            if (p.Command is null) p.Command = a;
            else p.Positionals.Add(a);
        }
        return p;
    }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public int? IntOption(string name)
    {
        var v = Option(name);
        if (v is null) return null;
        if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ToolkitException(ErrorKind.Usage, $"Option '--{name}' must be a whole number.");
        return n;
    }

    public (int From, int To)? RangeOption()
    {
        var v = Option("range");
        if (v is null) return null;
        var parts = v.Split('-', 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            throw new ToolkitException(ErrorKind.Usage, "Option '--range' must look like 0-4.");
        return (a, b);
    }

    /// <summary>Exactly <paramref name="count"/> positionals, or a usage error naming the command.</summary>
    public string[] Require(int count, string usage)
    {
        if (Positionals.Count != count)
            throw new ToolkitException(ErrorKind.Usage, $"Usage: {usage}");
        return Positionals.ToArray();
    }
}
