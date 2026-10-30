using System.Text.Json;
using System.Text.RegularExpressions;

namespace VsChatHistoryToolkit;

/// <summary>
/// Finds the "Solution:" line inside the IDE context of user.message events in events.jsonl. Returns null when
/// absent; the solution is never inferred from folders or chat text.
/// </summary>
public static partial class SolutionFinder
{
    private const int MaxLineChars = 4_000_000;
    private const int MaxLines = 200_000;

    public static string? Find(string eventsPath)
    {
        if (!File.Exists(eventsPath)) return null;

        using var stream = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var count = 0;
        while (reader.ReadLine() is { } line && count++ < MaxLines)
        {
            if (line.Length > MaxLineChars) continue;
            if (!line.Contains("Solution:", StringComparison.Ordinal) || !line.Contains("user.message", StringComparison.Ordinal))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!IsUserMessage(doc.RootElement)) continue;
                var found = Search(doc.RootElement);
                if (found is not null) return found;
            }
            catch (JsonException)
            {
                // A partially written trailing line is expected while Visual Studio is running.
            }
        }
        return null;
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
                return value.Length == 0 ? null : TextUtil.Cap(value, 200, out _);
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
