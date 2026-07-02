using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Hands out per-site <see cref="UniFiConnectionService"/> instances. The default
/// site's instance is the DI singleton (unchanged behavior for all existing
/// consumers); non-default site instances are created lazily with their slug and
/// live for the app's lifetime. Consumers that need another site's console
/// (site wizard, Sites page status, future background fan-out) resolve it here
/// instead of injecting UniFiConnectionService directly.
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
        if (slug == SiteManagementService.DefaultSiteSlug)
            return _serviceProvider.GetRequiredService<UniFiConnectionService>();

        return _connections.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<UniFiConnectionService>(_serviceProvider, s));
    }

    /// <summary>The default site's connection (the DI singleton).</summary>
    public UniFiConnectionService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    public void Dispose()
    {
        // Only instances created here; the default singleton is disposed by the container
        foreach (var connection in _connections.Values)
            connection.Dispose();
        _connections.Clear();
    }
}
