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

        var hits = new List<AppSearchHit>();

        foreach (var provider in _providers)
        {
            if (area is not null && !string.Equals(provider.Area, area, StringComparison.OrdinalIgnoreCase))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<AppSearchEntry> entries;
            try
            {
                entries = await provider.GetEntriesAsync(context);
            }
            catch (Exception ex)
            {
                // One area failing to describe itself must not take the search box down with it.
                _logger.LogWarning(ex, "Search provider for {Area} could not list its entries", provider.Area);
                continue;
            }

            foreach (var entry in entries)
            {
                var score = ScoreEntry(entry, query);
                if (score >= FuzzyMatch.MinimumUsefulScore)
                    hits.Add(new AppSearchHit(entry, score));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// The entry's best showing across its fields. The combined pass exists so a query whose words
    /// are split across fields still lands - "monitoring cable" is the tab plus part of the title,
    /// and no single field holds both.
    /// </summary>
    internal static int ScoreEntry(AppSearchEntry entry, string query)
    {
        var best = FuzzyMatch.Score(entry.Title, query);
        best = Math.Max(best, FuzzyMatch.ScoreBest(entry.Aliases, query) * AliasWeight / 100);
        best = Math.Max(best, FuzzyMatch.ScoreBest(entry.Keywords, query) * KeywordWeight / 100);

        var combined = string.Join(' ', new[] { entry.Title, entry.Section, entry.Area }
            .Concat(entry.Aliases)
            .Concat(entry.Keywords)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        return Math.Max(best, FuzzyMatch.Score(combined, query) * CombinedWeight / 100);
    }
}
