using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Per-site live monitoring caches. Scoped resolution of MonitoringLiveStats
/// forwards to the current site's instance so pages and endpoints read the
/// site they're looking at; the agent result sink records relayed data into
/// the owning site's instance; singleton collectors pin GetDefault(). Same
/// pattern as SiteConnectionRegistry / MonitoringInfluxRegistry. Instances
/// are pure in-memory caches, so the registry never needs to dispose them.
/// </summary>
public class MonitoringLiveStatsRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, MonitoringLiveStats> _instances = new();

    public MonitoringLiveStatsRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The live stats cache for a site, created on first use.</summary>
    public MonitoringLiveStats GetFor(string slug) =>
        _instances.GetOrAdd(slug, s => ActivatorUtilities.CreateInstance<MonitoringLiveStats>(
            _serviceProvider,
            // Empty slug = the default instance, reading targets from the main database.
            s == SiteManagementService.DefaultSiteSlug ? "" : s));

    /// <summary>The default site's cache.</summary>
    public MonitoringLiveStats GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
