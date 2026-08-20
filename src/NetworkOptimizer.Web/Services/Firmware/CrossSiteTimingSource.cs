using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Pools learned upgrade timings across the install's sites, so a model first seen at one site
/// improves estimates everywhere.
/// <para>
/// The site's own measurements win outright once it has enough of them; only a site that has never
/// upgraded a model (or has seen it once or twice) borrows the other sites' pooled window. Reads of
/// another site's database are best-effort - a missing or locked site DB costs a better estimate,
/// never the plan.
/// </para>
/// </summary>
public class CrossSiteTimingSource
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly ILogger _logger;
    private readonly string _siteSlug;

    /// <param name="mainDbFactory">Main database (the site registry).</param>
    /// <param name="siteDbFactory">Per-site database factory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">The site being planned for; its own rows are supplied by the caller.</param>
    public CrossSiteTimingSource(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        SiteDbContextFactory siteDbFactory,
        ILogger logger,
        string siteSlug)
    {
        _mainDbFactory = mainDbFactory;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <summary>
    /// This site's timings, filled in from the other sites where this one has too little history.
    /// </summary>
    /// <param name="ownTimings">The planning site's own learned timings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<FirmwareModelTiming>> MergeAsync(
        IEnumerable<FirmwareModelTiming> ownTimings, CancellationToken cancellationToken = default)
    {
        var own = ownTimings?.ToList() ?? [];
        var others = await ReadOtherSiteTimingsAsync(cancellationToken);
        return Merge(own, others);
    }

    /// <summary>
    /// The merge rule, with no database in sight.
    /// <para>
    /// A model the site has measured <see cref="FirmwareTimingEstimator.MinLearnedSamples"/> times
    /// keeps its own numbers untouched. Otherwise the other sites' rows for that model are pooled -
    /// each weighted by how many upgrades it represents - and used when the pool itself clears the
    /// same bar. Anything else is left exactly as the site had it.
    /// </para>
    /// </summary>
    /// <param name="own">The planning site's rows.</param>
    /// <param name="others">Rows from every other site, one row per site and model.</param>
    public static List<FirmwareModelTiming> Merge(
        IEnumerable<FirmwareModelTiming> own, IEnumerable<FirmwareModelTiming> others)
    {
        var merged = (own ?? []).ToDictionary(t => t.Model, StringComparer.OrdinalIgnoreCase);

        var pooledByModel = (others ?? [])
            .Where(t => !string.IsNullOrEmpty(t.Model) && t.SampleCount > 0 && t.MedianDowntimeSeconds > 0)
            .GroupBy(t => t.Model, StringComparer.OrdinalIgnoreCase);

        foreach (var group in pooledByModel)
        {
            if (merged.TryGetValue(group.Key, out var mine) &&
                mine.SampleCount >= FirmwareTimingEstimator.MinLearnedSamples &&
                mine.MedianDowntimeSeconds > 0)
            {
                continue;
            }

            var samples = group.Sum(t => t.SampleCount);
            if (samples < FirmwareTimingEstimator.MinLearnedSamples)
                continue;

            merged[group.Key] = new FirmwareModelTiming
            {
                Model = group.Key,
                SampleCount = samples,
                MedianDowntimeSeconds = WeightedMean(group, t => t.MedianDowntimeSeconds, samples),
                P90DowntimeSeconds = WeightedMean(group, t => t.P90DowntimeSeconds, samples),
                UpdatedAt = group.Max(t => t.UpdatedAt),
            };
        }

        return merged.Values.ToList();
    }

    private static int WeightedMean(
        IEnumerable<FirmwareModelTiming> rows, Func<FirmwareModelTiming, int> value, int totalSamples)
    {
        if (totalSamples <= 0) return 0;
        var weighted = rows.Sum(r => (long)value(r) * r.SampleCount);
        return (int)Math.Round((double)weighted / totalSamples);
    }

    /// <summary>Every other enabled site's learned timings, skipping any that will not open.</summary>
    private async Task<List<FirmwareModelTiming>> ReadOtherSiteTimingsAsync(CancellationToken cancellationToken)
    {
        var results = new List<FirmwareModelTiming>();
        List<(string Slug, bool IsDefault)> sites;

        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
            sites = (await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled)
                    .Select(s => new { s.Slug, s.IsDefault })
                    .ToListAsync(cancellationToken))
                .Select(s => (s.Slug, s.IsDefault))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list sites for cross-site firmware timings");
            return results;
        }

        if (!sites.Any(s => s.IsDefault))
            sites.Add((SiteManagementService.DefaultSiteSlug, true));

        foreach (var (slug, isDefault) in sites)
        {
            if (string.IsNullOrEmpty(slug) || string.Equals(slug, _siteSlug, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!isDefault && !_siteDbFactory.SiteDbExists(slug))
                continue;

            try
            {
                await using var siteDb = _siteDbFactory.CreateForSite(slug, isDefault);
                results.AddRange(await siteDb.FirmwareModelTimings.AsNoTracking().ToListAsync(cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read firmware timings from site {Slug}", slug);
            }
        }

        return results;
    }
}
