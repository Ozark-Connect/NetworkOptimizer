using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Owns one <see cref="IspHealthService"/> (and its <see cref="PhysicalLinkResolver"/>)
/// per site. The report snapshot, compute lock, custom-window cache, and adaptive
/// window state are all per-site; a single instance pinned to the default site put
/// the main site's ISP Health score on every site's Monitoring page. Scoped
/// resolution forwards to the current site's instance, same pattern as
/// MonitoringInfluxRegistry / MonitoringCollectionRegistry.
/// </summary>
public class IspHealthRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, IspHealthService> _instances = new(StringComparer.OrdinalIgnoreCase);

    public IspHealthRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The ISP Health service for a site, created on first use.</summary>
    public IspHealthService GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            var resolver = ActivatorUtilities.CreateInstance<PhysicalLinkResolver>(_serviceProvider, s);
            return ActivatorUtilities.CreateInstance<IspHealthService>(_serviceProvider, s, resolver);
        });

    /// <summary>The default site's ISP Health service.</summary>
    public IspHealthService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
