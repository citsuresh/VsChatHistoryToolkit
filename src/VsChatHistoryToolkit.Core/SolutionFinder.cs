using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VsChatHistoryToolkit;

/// <param name="Solution">The solution text, or null when none was found.</param>
/// <param name="Incomplete">True when the scan stopped early (line or length cap), so "not found" is not proven.</param>
/// <param name="SolutionCut">True when the solution text was longer than the cap and was shortened.</param>
public sealed record SolutionScan(string? Solution, bool Incomplete, bool SolutionCut = false);

/// <summary>
/// Finds the "Solution:" line inside the IDE context of user.message events in events.jsonl. Returns null when
/// absent; the solution is never inferred from folders or chat text. Lines are read in bounded chunks so one huge
/// line cannot be pulled into memory, and a scan that hits a cap says so. Line ends are \n, \r\n or a lone \r,
/// like <see cref="StreamReader.ReadLine"/>; terminators never count toward a line's length.
/// </summary>
public static partial class SolutionFinder
{
    internal const int MaxLineChars = 4_000_000;
    internal const int MaxLines = 200_000;

    public static SolutionScan Scan(string eventsPath)
    {
        if (!File.Exists(eventsPath)) return new SolutionScan(null, false);

        using var stream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var buffer = new char[8192];
        var line = new StringBuilder();
        var skipping = false;
        var incomplete = false;
        var lines = 0;
        var lastWasCr = false;

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (c == '\n' && lastWasCr)
                {
                    lastWasCr = false; // second half of CRLF, already ended the line
                    continue;
                }

                // Any further input after MaxLines complete lines means the file was not fully scanned.
                if (lines >= MaxLines) return new SolutionScan(null, true);

                lastWasCr = c == '\r';
                if (c == '\r' || c == '\n')
                {
                    if (!skipping && Inspect(line.ToString()) is { } found) return Found(found);
                    line.Clear();
                    skipping = false;
                    lines++;
                    continue;
                }

                if (skipping) continue;
                if (line.Length >= MaxLineChars)
                {
                    skipping = true;
                    incomplete = true;
                    line.Clear();
                    continue;
                }
                line.Append(c);
            }
        }

        if (!skipping && line.Length > 0 && Inspect(line.ToString()) is { } last) return Found(last);
        return new SolutionScan(null, incomplete);
    }

    private static SolutionScan Found(string solution)
    {
        var text = TextUtil.Cap(solution, Caps.SnippetChars, out var cut);
        return new SolutionScan(text, false, cut);
    }

    private static string? Inspect(string line)
    {
        if (!line.Contains("Solution:", StringComparison.Ordinal) || !line.Contains("user.message", StringComparison.Ordinal))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            return IsUserMessage(doc.RootElement) ? Search(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            // A partially written trailing line is expected while Visual Studio is running.
            return null;
        }
    }

    private static bool IsUserMessage(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && (type.GetString() ?? "").Contains("user.message", StringComparison.OrdinalIgnoreCase);

    private static string? Search(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var m = SolutionRx().Match(el.GetString() ?? "");
                if (!m.Success) return null;
                var value = m.Groups[1].Value.Trim();
                return value.Length == 0 ? null : value;
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    if (Search(p.Value) is { } o) return o;
                return null;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (Search(item) is { } a) return a;
                return null;
            default:
                return null;
        }
    }

    [GeneratedRegex(@"^[ \t>*\-]*Solution:[ \t]*(.+?)[ \t\r]*$", RegexOptions.Multiline)]
    private static partial Regex SolutionRx();
}
