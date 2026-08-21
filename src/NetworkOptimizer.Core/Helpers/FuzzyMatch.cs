namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Ranks candidate strings against what someone typed into a search box. A term matches when its
/// characters appear in the candidate in order, so "extspd" still finds "External Speed Test
/// Servers", but a contiguous run scores far higher than a scattered one and a word boundary higher
/// again - which is what keeps a loose subsequence from outranking the thing the user meant.
///
/// Scores are relative and only comparable within a single query.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>
    /// Below this a match is technically a subsequence but not what anyone meant, e.g. "ont"
    /// scattered through "Connection". Callers should discard anything under it.
    /// </summary>
    public const int MinimumUsefulScore = 40;

    private static readonly char[] TermSeparators = [' ', '\t', '\n', '\r'];

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/>, higher being better,
    /// or 0 when it does not match. A multi-word query requires every word to match somewhere in
    /// the candidate, in any order; the result is the per-word average so a threshold holds
    /// whatever the query's length.
    /// </summary>
    public static int Score(string? candidate, string? query)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(query))
            return 0;

        var terms = query.ToLowerInvariant().Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return 0;

        var text = candidate.ToLowerInvariant();
        var total = 0;
        foreach (var term in terms)
        {
            var score = ScoreTerm(text, term);
            if (score == 0)
                return 0;
            total += score;
        }

        return total / terms.Length;
    }

    /// <summary>The best score <paramref name="query"/> reaches against any of the candidates.</summary>
    public static int ScoreBest(IEnumerable<string?> candidates, string? query)
    {
        var best = 0;
        foreach (var candidate in candidates)
            best = Math.Max(best, Score(candidate, query));
        return best;
    }

    private static int ScoreTerm(string text, string term)
    {
        // Every occurrence, not just the first: "ont" is inside "front" before it is the start of
        // "ONT", and the word-start one is the one the typist meant.
        var best = 0;
        for (var index = text.IndexOf(term, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(term, index + 1, StringComparison.Ordinal))
        {
            if (index == 0)
                return 100 + term.Length * 6;

            best = Math.Max(best, IsWordStart(text, index)
                ? 88 + term.Length * 6
                // Buried inside a word. "ont" really is in "controller", but nobody typing it means
                // that card, so a short one lands under the useful threshold on its own.
                : 24 + term.Length * 4);
        }

        return best > 0 ? best : SubsequenceScore(text, term);
    }

    private static int SubsequenceScore(string text, string term)
    {
        var next = 0;
        var score = 0;
        var run = 0;

        for (var i = 0; i < text.Length && next < term.Length; i++)
        {
            if (text[i] != term[next])
            {
                run = 0;
                continue;
            }

            score += 4 + Math.Min(run, 4) * 4;
            if (IsWordStart(text, i))
                score += 10;
            run++;
            next++;
        }

        if (next < term.Length)
            return 0;

        // Penalize the characters the term did not account for, so a short title beats a long one
        // that happens to contain the same letters in the same order.
        return Math.Max(1, score - (text.Length - term.Length) / 2);
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);
}
