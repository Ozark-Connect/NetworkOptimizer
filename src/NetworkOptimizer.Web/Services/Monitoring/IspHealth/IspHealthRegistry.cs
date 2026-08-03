using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Owns one <see cref="IspHealthService"/> (and its <see cref="PhysicalLinkResolver"/>)
/// per (site, WAN). The report snapshot, compute lock, custom-window cache, and adaptive
/// window state are all per-instance; a single instance pinned to the default site put
/// the main site's ISP Health score on every site's Monitoring page. The WAN dimension
/// keys one report per graded WAN: the null/absent WAN is the configured-primary
/// instance every install has (single-WAN sites never create another), and the WAN
/// selectors resolve non-primary WANs by their UniFi wan key ("wan2"). Scoped
/// resolution forwards to the current site's primary instance, same pattern as
/// MonitoringInfluxRegistry / MonitoringCollectionRegistry.
/// </summary>
public class IspHealthRegistry : ISiteScopedRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, IspHealthService> _instances = new(StringComparer.OrdinalIgnoreCase);

    public IspHealthRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    // Composite key: "{slug}" for the primary instance (identical to the pre-multi-WAN key, so
    // nothing about the primary path changes), "{slug}|{wanKey}" for a scoped WAN. The slug
    // alphabet has no '|', so keys cannot collide, and EvictSite can sweep by prefix.
    private static string Key(string slug, string? wanInterface) =>
        string.IsNullOrWhiteSpace(wanInterface) ? slug : $"{slug}|{wanInterface.Trim().ToLowerInvariant()}";

    /// <summary>The site's primary-WAN ISP Health service, created on first use.</summary>
    public IspHealthService GetFor(string slug) => GetFor(slug, null);

    /// <summary>
    /// The ISP Health service grading one WAN of a site, created on first use. Null (or empty)
    /// <paramref name="wanInterface"/> is the configured-primary instance; a UniFi wan key
    /// ("wan2") grades that WAN alone.
    /// </summary>
    public IspHealthService GetFor(string slug, string? wanInterface) =>
        _instances.GetOrAdd(Key(slug, wanInterface), _ =>
        {
            var resolver = ActivatorUtilities.CreateInstance<PhysicalLinkResolver>(_serviceProvider, slug);
            return string.IsNullOrWhiteSpace(wanInterface)
                ? ActivatorUtilities.CreateInstance<IspHealthService>(_serviceProvider, slug, resolver)
                : ActivatorUtilities.CreateInstance<IspHealthService>(_serviceProvider, slug, resolver,
                    wanInterface.Trim().ToLowerInvariant());
        });

    /// <summary>The default site's primary-WAN ISP Health service.</summary>
    public IspHealthService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <inheritdoc />
    /// <remarks>Sweeps every WAN instance of the site, not just the primary.</remarks>
    public Func<ValueTask>? EvictSite(string slug)
    {
        foreach (var key in _instances.Keys)
        {
            if (string.Equals(key, slug, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(slug + "|", StringComparison.OrdinalIgnoreCase))
            {
                _instances.TryRemove(key, out _);
            }
        }
        return null;
    }
}
