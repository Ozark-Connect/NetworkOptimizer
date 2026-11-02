using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>The Edit tab's Collections search, counted on the server for the collections not in the editor.</summary>
public static class CcFind
{
    private static readonly Regex SecretToken = new(@"<secret:[^>\s]+>", RegexOptions.CultureInvariant);

    /// <summary>
    /// Case-insensitive, non-overlapping matches of <paramref name="query"/> in a collection's YAML, which
    /// holds field names and values alike. Masked secret tokens are skipped. Must agree with findMatches in
    /// cc-editor.js, which highlights the open collection.
    /// </summary>
    public static int Count(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return 0;
        var tokens = SecretToken.Matches(text).Select(m => (From: m.Index, To: m.Index + m.Length)).ToList();
        var count = 0;
        foreach (Match m in Regex.Matches(text, Regex.Escape(query), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int from = m.Index, to = m.Index + m.Length;
            if (!tokens.Any(t => from < t.To && to > t.From)) count++;
        }
        return count;
    }
}
