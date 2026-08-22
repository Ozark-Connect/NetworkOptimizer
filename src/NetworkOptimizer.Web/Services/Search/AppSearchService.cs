using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.Search;

/// <summary>Ranks what the registered <see cref="IAppSearchProvider"/>s offer against a typed query.</summary>
public interface IAppSearchService
{
    /// <summary>
    /// The best matches for <paramref name="query"/>, best first. Pass <paramref name="area"/> to
    /// search one area only; leave it null to search everything registered.
    /// </summary>
    Task<IReadOnlyList<AppSearchHit>> SearchAsync(
        string? query,
        AppSearchContext context,
        int maxResults = 8,
        string? area = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Not a <c>[MutatingService]</c>: it mutates nothing and returns only navigation metadata that is
/// compiled into the app. What it must not do is offer a target the caller cannot reach, and that
/// is the providers' job - they filter their own entries against the <see cref="AppSearchContext"/>.
/// </summary>
public sealed class AppSearchService : IAppSearchService
{
    // A title hit is the answer someone typed the title expecting. The rest are weighted down so a
    // keyword buried in a card never outranks the card actually named that.
    private const int AliasWeight = 90;
    private const int KeywordWeight = 75;
    private const int CombinedWeight = 55;

    private readonly IEnumerable<IAppSearchProvider> _providers;
    private readonly ILogger<AppSearchService> _logger;

    public AppSearchService(IEnumerable<IAppSearchProvider> providers, ILogger<AppSearchService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppSearchHit>> SearchAsync(
        string? query,
        AppSearchContext context,
        int maxResults = 8,
        string? area = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            return [];

        var hits = new List<(AppSearchHit Hit, FuzzyMatch.MatchResult Match)>();

        foreach (var provider in _providers)
        {
            if (area is not null && !string.Equals(provider.Area, area, StringComparison.OrdinalIgnoreCase))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<AppSearchEntry> entries;
            try
            {
                entries = await provider.GetEntriesAsync(context, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Superseded typing, not a broken provider. Let it end the whole search.
                throw;
            }
            catch (Exception ex)
            {
                // One area failing to describe itself must not take the search box down with it.
                _logger.LogWarning(ex, "Search provider for {Area} could not list its entries", provider.Area);
                continue;
            }

            foreach (var entry in entries)
            {
                var match = MatchEntry(entry, query);
                if (match.Matched > 0 && match.Score >= FuzzyMatch.MinimumUsefulScore)
                    hits.Add((new AppSearchHit(entry, match.Score), match));
            }
        }

        // Everything the query asked for, if anything answers all of it. Otherwise the best of what
        // is left, most of the query first - someone typing "stop flagging my printer" is owed the
        // Security Audit rather than an empty list, and only the words we know can get them there.
        var complete = hits.Where(h => h.Match.IsComplete).ToList();
        var chosen = complete.Count > 0 ? complete : hits;

        return chosen
            .OrderByDescending(h => h.Match.Matched)
            .ThenByDescending(h => h.Match.Score)
            .ThenBy(h => h.Hit.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(h => h.Hit)
            .ToList();
    }

    /// <summary>
    /// The entry's best showing across its fields, then across all of them at once so a query whose
    /// words are split between them still lands. Best means most of the query matched first, and
    /// only then the better score - a result that answers more of what was typed wins.
    /// </summary>
    internal static FuzzyMatch.MatchResult MatchEntry(AppSearchEntry entry, string query)
    {
        var best = FuzzyMatch.Match(entry.Title, query);

        foreach (var alias in entry.Aliases)
            best = Better(best, Weighted(FuzzyMatch.Match(alias, query), AliasWeight));

        foreach (var keyword in entry.Keywords)
            best = Better(best, Weighted(FuzzyMatch.Match(keyword, query), KeywordWeight));

        return Better(best, Weighted(FuzzyMatch.Match(entry.SearchText, query), CombinedWeight));
    }

    /// <summary>The strict score: 0 unless every word of the query landed on this entry.</summary>
    internal static int ScoreEntry(AppSearchEntry entry, string query)
    {
        var match = MatchEntry(entry, query);
        return match.IsComplete ? match.Score : 0;
    }

    private static FuzzyMatch.MatchResult Weighted(FuzzyMatch.MatchResult match, int weight) =>
        match with { Score = match.Score * weight / 100 };

    /// <summary>
    /// Which of two readings of the same entry to keep. Matching more of the query wins, but only
    /// among readings worth showing at all - otherwise a field that technically contains every word
    /// and means none of them shadows the keyword that is the actual answer.
    /// </summary>
    private static FuzzyMatch.MatchResult Better(FuzzyMatch.MatchResult a, FuzzyMatch.MatchResult b) =>
        (Useful(b), b.Matched, b.Score).CompareTo((Useful(a), a.Matched, a.Score)) > 0 ? b : a;

    private static int Useful(FuzzyMatch.MatchResult match) =>
        match.Score >= FuzzyMatch.MinimumUsefulScore ? 1 : 0;
}
