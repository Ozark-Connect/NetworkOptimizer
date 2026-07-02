using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="UniFiConnectionService"/> instances, created
/// lazily per slug and alive for the app's lifetime. Scoped/component consumers
/// receive the current site's instance through the scoped forwarding
/// registration; singletons and background code inject this registry and use
/// GetDefault() or GetFor(slug).
/// </summary>
public class SiteConnectionRegistry : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, UniFiConnectionService> _connections = new();

    public SiteConnectionRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>Connection instance for a site by slug.</summary>
    public UniFiConnectionService GetFor(string slug)
    {
        return _connections.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<UniFiConnectionService>(_serviceProvider, s));
    }

    /// <summary>The default site's connection.</summary>
    public UniFiConnectionService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    public void Dispose()
    {
        foreach (var connection in _connections.Values)
            connection.DisposeOwned();
        _connections.Clear();
    }
}
