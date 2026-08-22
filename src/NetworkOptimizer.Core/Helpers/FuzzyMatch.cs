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

    // Only ever applied to a query of more than three words. Deliberately function words and
    // nothing else: anything that could name a setting has to survive.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "can", "do", "does", "for", "from", "how", "i",
        "in", "is", "it", "me", "my", "need", "of", "on", "or", "please", "the", "then", "this",
        "to", "want", "we", "what", "when", "where", "why", "with", "you", "your",
    };

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="candidate"/>, higher being better,
    /// or 0 when any word of the query fails to land. Use <see cref="Match"/> where a partial
    /// answer beats none.
    /// </summary>
    public static int Score(string? candidate, string? query)
    {
        var match = Match(candidate, query);
        return match.IsComplete ? match.Score : 0;
    }

    /// <summary>
    /// How well <paramref name="query"/> fits <paramref name="candidate"/>, word by word. The score
    /// averages only the words that landed, so a long query is not dragged down by the ones that did
    /// not, and <see cref="MatchResult.IsComplete"/> says whether any were left behind.
    /// </summary>
    public static MatchResult Match(string? candidate, string? query)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(query))
            return default;

        var terms = SignificantTerms(query);
        if (terms.Length == 0)
            return default;

        var text = candidate.ToLowerInvariant();
        var total = 0;
        var matched = 0;
        foreach (var term in terms)
        {
            var score = ScoreTerm(text, term);
            if (score == 0)
                continue;
            total += score;
            matched++;
        }

        return matched == 0 ? default : new MatchResult(total / matched, matched, terms.Length);
    }

    /// <summary>
    /// The words worth matching on. More than three words is someone typing a question rather than
    /// a name, so the ones carrying no meaning alone come out - otherwise "how do I allow my apple
    /// tv on the main network" fails on "how" and finds nothing at all. Short queries are left
    /// alone: "log in" is two stopwords away from nothing.
    /// </summary>
    private static string[] SignificantTerms(string query)
    {
        var terms = query.ToLowerInvariant().Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length <= 3)
            return terms;

        var significant = terms.Where(t => !Stopwords.Contains(t)).ToArray();
        return significant.Length > 0 ? significant : terms;
    }

    /// <summary>What a query matched against one candidate: its score, and how much of it landed.</summary>
    public readonly record struct MatchResult(int Score, int Matched, int Terms)
    {
        /// <summary>True when every word of the query found something.</summary>
        public bool IsComplete => Terms > 0 && Matched == Terms;
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
        // that happens to contain the same letters in the same order. Once that wipes the score out
        // the match is not a weak one, it is nothing: any short word is a subsequence of a long
        // enough haystack, and calling that a match let "stop flagging my printer" match every card
        // on the page. Never floor this at 1.
        var penalized = score - (text.Length - term.Length) / 2;
        return penalized > 0 ? penalized : 0;
    }

    private static bool IsWordStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]);
}
