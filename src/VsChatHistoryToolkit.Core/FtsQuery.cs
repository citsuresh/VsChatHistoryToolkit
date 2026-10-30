using System.Text;

namespace VsChatHistoryToolkit;

/// <summary>
/// Turns free text into a safe FTS5 MATCH expression. FTS5 matches word tokens, so anything that is not a
/// letter or digit separates terms. Each term is quoted so operators and punctuation (#, -, (, AND, NOT) are literal.
/// </summary>
public static class FtsQuery
{
    public static IReadOnlyList<string> Tokenize(string input, out bool truncated)
    {
        var cut = false;
        var terms = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            var term = sb.ToString();
            sb.Clear();
            if (term.Length > Caps.MaxTermLength)
            {
                term = term[..Caps.MaxTermLength];
                cut = true;
            }
            if (terms.Count < Caps.MaxSearchTerms) terms.Add(term);
            else cut = true;
        }

        foreach (var ch in input)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else Flush();
        }
        Flush();

        truncated = cut;
        return terms;
    }

    public static string ToMatch(IEnumerable<string> terms) =>
        string.Join(" ", terms.Select(t => "\"" + t + "\""));
}
