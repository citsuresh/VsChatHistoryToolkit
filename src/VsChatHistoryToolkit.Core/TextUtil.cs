using System.Text;

namespace VsChatHistoryToolkit;

internal static class TextUtil
{
    public static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>Cuts to at most <paramref name="max"/> characters (never splitting a surrogate pair) and appends an ellipsis when cut.</summary>
    public static string Cap(string s, int max, out bool cut)
    {
        cut = false;
        if (max < 0) max = 0;
        if (s.Length <= max) return s;
        cut = true;
        var end = max;
        if (end > 0 && char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end] + "…";
    }
}

/// <summary>Shared character allowance for one response, so many small fields cannot add up to a whole chat.</summary>
internal sealed class CharBudget
{
    private int _left;

    public CharBudget(int total) => _left = total;

    public string? Take(string? s, int perField, ref bool truncated)
    {
        if (s is null) return null;
        var limit = Math.Max(0, Math.Min(perField, _left));
        var result = TextUtil.Cap(s, limit, out var cut);
        _left -= Math.Min(s.Length, limit);
        if (cut) truncated = true;
        return result;
    }
}
