using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>
/// Per-site owner of <see cref="LanFlowMapCache"/> instances. The map snapshot,
/// live-rate dictionary, and historic playback cache are all per-site state; a
/// single global cache let a secondary site's rebuild overwrite the main site's
/// map (and drove the "MonitoringSettings not configured" flood as the secondary
/// rebuild queried its empty Influx client). Scoped resolution forwards to the
/// current site's cache, so each site keeps its own snapshot and build lock.
/// </summary>
public class LanFlowMapCacheRegistry
{
    private readonly ConcurrentDictionary<string, LanFlowMapCache> _caches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The map cache for a site, created on first use.</summary>
    public LanFlowMapCache GetFor(string slug) => _caches.GetOrAdd(slug, _ => new LanFlowMapCache());

    /// <summary>The default site's map cache.</summary>
    public LanFlowMapCache GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
