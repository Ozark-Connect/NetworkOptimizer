using System.Collections.Concurrent;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Per-site monitoring alert evaluators. The evaluators are in-memory state
/// machines keyed by target id / device MAC / port, and those keys repeat
/// across sites (every site seeds a "wan-cloudflare-1111" target), so state
/// must never be shared: each site gets its own evaluator bundle, created
/// lazily. Non-default bundles stamp their site slug into alert titles. Both
/// the site's local collection loops and the agent tunnel result sink evaluate
/// through the owning site's bundle. Same ownership pattern as
/// SiteConnectionRegistry / MonitoringInfluxRegistry; instances are pure
/// in-memory state, so nothing needs disposal.
/// </summary>
public class MonitoringAlertRegistry
{
    /// <summary>One site's alert evaluators.</summary>
    public sealed record SiteAlertEvaluators(
        MonitoringAlertEvaluator Targets,
        DeviceHealthAlertEvaluator DeviceHealth,
        SfpAlertEvaluator Sfp);

    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SiteAlertEvaluators> _instances = new();

    public MonitoringAlertRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The alert evaluators for a site, created on first use.</summary>
    public SiteAlertEvaluators GetFor(string slug) =>
        _instances.GetOrAdd(slug, s => new SiteAlertEvaluators(
            ActivatorUtilities.CreateInstance<MonitoringAlertEvaluator>(_serviceProvider, s),
            ActivatorUtilities.CreateInstance<DeviceHealthAlertEvaluator>(_serviceProvider, s),
            ActivatorUtilities.CreateInstance<SfpAlertEvaluator>(_serviceProvider, s)));

    /// <summary>The default site's evaluators.</summary>
    public SiteAlertEvaluators GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
