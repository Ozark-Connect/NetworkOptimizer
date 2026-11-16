using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>
/// Traces a monitored target and stores its ancestry and route: the one path every writer takes
/// (trace-on-save, the hourly witness backfill, the Path Map's passes). It traces from the vantage
/// and bind that measure the target, in the site's single trace slot, and asks for an early
/// re-discovery when the trace confirms a path shift. The app probing, not a change to any
/// configuration, so it writes no audit entry.
/// </summary>
public sealed class PathRouteRecorder
{
    private readonly PathEpisodeRegistry _episodes;
    private readonly SiteDbContextFactory _siteDb;
    private readonly RoutePlacer _placer;
    private readonly ILogger<PathRouteRecorder> _logger;

    public PathRouteRecorder(PathEpisodeRegistry episodes, SiteDbContextFactory siteDb, RoutePlacer placer,
        ILogger<PathRouteRecorder> logger)
    {
        _episodes = episodes;
        _siteDb = siteDb;
        _placer = placer;
        _logger = logger;
    }

    /// <summary>Traces one target on a site and stores what the trace found.</summary>
    public async Task<AncestryTrace> TraceAsync(string slug, int targetId, CancellationToken ct = default)
    {
        var isDefault = slug == SiteManagementService.DefaultSiteSlug;
        await using var db = _siteDb.CreateForSite(slug, isDefault);
        var target = await db.MonitoringTargets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == targetId, ct);
        if (target == null || string.IsNullOrEmpty(target.Address)) return new AncestryTrace(false, null, null);
        var context = target.WanContextId is int ctxId
            ? await db.WanContexts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ctxId, ct)
            : null;

        var watcher = _episodes.GetFor(slug);
        var vantage = watcher.VantageFor(target, context);
        var executor = watcher.ExecutorFor(vantage);
        var witness = target.TargetType is MonitoringTargetType.InternetService or MonitoringTargetType.Custom;
        var result = await watcher.InTraceSlotAsync(
            () => TargetAncestry.TraceAsync(executor, db, slug, target.Id, target.Address, vantage, _placer, witness, _logger, ct), ct);

        if (result.Route != null) await _placer.LearnAsync(slug, result.Route, ct);

        if (result.Shift != null)
        {
            _logger.LogInformation("Path Map: {Target}'s route shifted on site {Slug}", target.Name, slug);
            try { await ShiftRediscovery.RequestAsync(db, context?.WanInterface, DateTime.UtcNow, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not ask for an early re-discovery on site {Slug}", slug);
            }
        }
        return result;
    }
}
