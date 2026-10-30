using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VsChatHistoryToolkit;

public static partial class TitleRules
{
    /// <summary>Trims, turns line breaks and tabs into spaces, drops other control/format characters, and enforces 1..120 characters.</summary>
    public static string Normalize(string? raw)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw ?? "")
        {
            var cat = char.GetUnicodeCategory(ch);
            if (ch is '\r' or '\n' or '\t' || cat is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                sb.Append(' ');
            else if (char.IsControl(ch) || cat == UnicodeCategory.Format)
                continue;
            else
                sb.Append(ch);
        }

        var title = sb.ToString().Trim();
        if (title.Length == 0)
            throw new ToolkitException(ErrorKind.Usage, "Title is empty after removing control characters.");
        if (title.Length > Caps.RenameTitleMaxLength)
            throw new ToolkitException(ErrorKind.Usage, $"Title is {title.Length} characters; the maximum is {Caps.RenameTitleMaxLength}.");
        return title;
    }

    /// <summary>
    /// Formats the value for a "name:" line. Plain scalar when that is unambiguous, otherwise a double-quoted
    /// YAML scalar. Visual Studio's own parser for this field is unverified, so plain is preferred.
    /// </summary>
    public static string FormatYamlValue(string title)
    {
        if (IsPlainSafe(title)) return title;

        var sb = new StringBuilder("\"");
        foreach (var ch in title)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static bool IsPlainSafe(string s) =>
        PlainRx().IsMatch(s) && !ReservedRx().IsMatch(s) && !NumberRx().IsMatch(s);

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} _.,()/+=!?@'&%;\-]*$")]
    private static partial Regex PlainRx();

    [GeneratedRegex(@"^(true|false|yes|no|on|off|null|y|n)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedRx();

    [GeneratedRegex(@"^[-+]?(\d[\d_]*)?\.?\d+([eE][-+]?\d+)?$")]
    private static partial Regex NumberRx();
}
