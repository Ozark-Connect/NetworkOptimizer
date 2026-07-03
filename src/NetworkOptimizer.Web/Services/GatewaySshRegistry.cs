using System.Collections.Concurrent;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="GatewaySshService"/> instances, created lazily
/// per slug. Scoped resolution of IGatewaySshService forwards to the current
/// site's instance, so pages and scoped deployment services talk to the
/// gateway of the site they are showing; singleton consumers inject this
/// registry and pin GetDefault() (or GetFor for site-bound instances). Same
/// ownership pattern as SiteConnectionRegistry. Instances hold no connections
/// (SSH sessions are per-command), so nothing needs disposal.
/// </summary>
public class GatewaySshRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, GatewaySshService> _instances = new();

    public GatewaySshRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The gateway SSH service for a site, created on first use.</summary>
    public GatewaySshService GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<GatewaySshService>(_serviceProvider, s));

    /// <summary>The default site's gateway SSH service.</summary>
    public GatewaySshService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
