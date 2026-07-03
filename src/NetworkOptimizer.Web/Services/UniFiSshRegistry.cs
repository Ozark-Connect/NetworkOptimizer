using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="UniFiSshService"/> instances (shared device SSH
/// credentials + per-device configurations, read from each site's own database),
/// created lazily per slug. Scoped resolution of UniFiSshService forwards to the
/// current site's instance; singleton consumers inject this registry and pin
/// GetDefault() or GetFor(slug). Same ownership pattern as GatewaySshRegistry.
/// Instances hold no connections (SSH sessions are per-command), so nothing
/// needs disposal.
/// </summary>
public class UniFiSshRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, UniFiSshService> _instances = new();

    public UniFiSshRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The device SSH service for a site, created on first use.</summary>
    public UniFiSshService GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<UniFiSshService>(_serviceProvider, s));

    /// <summary>The default site's device SSH service.</summary>
    public UniFiSshService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
