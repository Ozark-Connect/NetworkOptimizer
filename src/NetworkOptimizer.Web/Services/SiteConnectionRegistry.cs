using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="UniFiConnectionService"/> instances, created
/// lazily per slug and alive for the app's lifetime. Scoped/component consumers
/// receive the current site's instance through the scoped forwarding
/// registration; singletons and background code inject this registry and use
/// GetDefault() or GetFor(slug).
/// </summary>
public class SiteConnectionRegistry : IDisposable, ISiteScopedRegistry
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

    /// <summary>
    /// Disposes and forgets a site's connection (site disabled or removed).
    /// A later GetFor recreates it fresh - e.g. when the site is re-enabled.
    /// </summary>
    public void RemoveFor(string slug)
    {
        if (_connections.TryRemove(slug, out var connection))
            connection.DisposeOwned();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Teardown is deferred rather than done here: the site's tracer, speed test bundle and
    /// gateway SSH service all hold this connection, so it must not be disposed until they have
    /// been evicted too. <see cref="RemoveFor"/> stays for the disable path, where the site and
    /// everything holding it remain in place.
    /// </remarks>
    public Func<ValueTask>? EvictSite(string slug)
        => _connections.TryRemove(slug, out var connection)
            ? () => { connection.DisposeOwned(); return ValueTask.CompletedTask; }
            : null;

    public void Dispose()
    {
        foreach (var connection in _connections.Values)
            connection.DisposeOwned();
        _connections.Clear();
    }
}
