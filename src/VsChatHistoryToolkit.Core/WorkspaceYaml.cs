using System.Text;
using System.Text.RegularExpressions;

namespace VsChatHistoryToolkit;

/// <summary>
/// Line-based view of workspace.yaml. Every line keeps its own terminator and the file keeps its BOM, so a file
/// that is parsed and written back unchanged is byte-for-byte identical. Only top-level "key: value" lines are
/// understood; nothing is re-serialised.
/// </summary>
public sealed partial class WorkspaceYaml
{
    private sealed class Line
    {
        public Line(string text, string eol) { Text = text; Eol = eol; }
        public string Text { get; set; }
        public string Eol { get; set; }
    }

    private readonly List<Line> _lines;
    private readonly bool _bom;

    private WorkspaceYaml(List<Line> lines, bool bom)
    {
        _lines = lines;
        _bom = bom;
    }

    /// <param name="strict">When true, invalid UTF-8 is refused (needed before rewriting); when false it is decoded leniently (reads).</param>
    public static WorkspaceYaml Parse(byte[] bytes, bool strict)
    {
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = bom ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, strict).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            throw new ToolkitException(ErrorKind.Refused, "workspace.yaml is not valid UTF-8; refusing to rewrite it.");
        }

        var lines = new List<Line>();
        var pos = 0;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                lines.Add(new Line(text[pos..], ""));
                break;
            }
            var content = text[pos..nl];
            var eol = "\n";
            if (content.EndsWith('\r'))
            {
                content = content[..^1];
                eol = "\r\n";
            }
            lines.Add(new Line(content, eol));
            pos = nl + 1;
        }
        return new WorkspaceYaml(lines, bom);
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.Append(l.Text).Append(l.Eol);
        var body = new UTF8Encoding(false).GetBytes(sb.ToString());
        if (!_bom) return body;
        var result = new byte[body.Length + 3];
        result[0] = 0xEF; result[1] = 0xBB; result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }

    /// <summary>How many top-level lines define the key (more than one makes a rewrite ambiguous).</summary>
    public int CountKey(string key) =>
        _lines.Count(l => KeyRx().Match(l.Text) is { Success: true } m && m.Groups[1].Value == key);

    /// <summary>The whole line (without terminator) for a top-level key, or null.</summary>
    public string? GetLine(string key)
    {
        var i = IndexOf(key);
        return i < 0 ? null : _lines[i].Text;
    }

    /// <summary>The unquoted value for a top-level key, or null if the key is absent.</summary>
    public string? GetValue(string key)
    {
        var line = GetLine(key);
        return line is null ? null : ValueOf(line, key);
    }

    public static string ValueOf(string line, string key) => Unquote(line[(key.Length + 1)..]);

    /// <summary>Replaces the key's line in place (keeping its terminator) or appends it at the end.</summary>
    public void SetLine(string key, string lineText)
    {
        var i = IndexOf(key);
        if (i >= 0)
        {
            _lines[i].Text = lineText;
            return;
        }

        var eol = DominantEol();
        if (_lines.Count > 0 && _lines[^1].Eol.Length == 0)
        {
            // File had no trailing newline: terminate the old last line, keep the new last line unterminated.
            _lines[^1].Eol = eol;
            _lines.Add(new Line(lineText, ""));
        }
        else
        {
            _lines.Add(new Line(lineText, eol));
        }
    }

    public void RemoveLine(string key)
    {
        var i = IndexOf(key);
        if (i < 0) return;
        var removed = _lines[i];
        _lines.RemoveAt(i);
        // Removing the last line hands its terminator back to the new last line, restoring the original ending.
        if (i == _lines.Count && _lines.Count > 0) _lines[^1].Eol = removed.Eol;
    }

    public static string Unquote(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            var sb = new StringBuilder();
            for (var i = 1; i < raw.Length - 1; i++)
            {
                var c = raw[i];
                if (c != '\\' || i + 1 >= raw.Length - 1) { sb.Append(c); continue; }
                var n = raw[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u' when i + 4 < raw.Length - 1 && int.TryParse(raw.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var cp):
                        sb.Append((char)cp);
                        i += 4;
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return raw[1..^1].Replace("''", "'");

        var hash = raw.IndexOf(" #", StringComparison.Ordinal);
        return hash >= 0 ? raw[..hash].TrimEnd() : raw;
    }

    private int IndexOf(string key) =>
        _lines.FindIndex(l => KeyRx().Match(l.Text) is { Success: true } m && m.Groups[1].Value == key);

    private string DominantEol() => _lines.FirstOrDefault(l => l.Eol.Length > 0)?.Eol ?? "\n";

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_\-]*):(?:[ \t]|$)")]
    private static partial Regex KeyRx();
}
