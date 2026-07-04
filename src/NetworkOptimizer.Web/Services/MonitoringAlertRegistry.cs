using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Alerts.Events;
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
        SfpAlertEvaluator Sfp,
        CableModemAlertEvaluator CableModem,
        OntAlertEvaluator Ont,
        CellularAlertEvaluator Cellular);

    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SiteAlertEvaluators> _instances = new();

    public MonitoringAlertRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The alert evaluators for a site, created on first use.</summary>
    public SiteAlertEvaluators GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            // Wrap the shared bus so every event these per-site evaluators publish is
            // stamped with this site's slug, routing it to the site's rules and channels.
            var bus = new SiteAlertEventBus(_serviceProvider.GetRequiredService<IAlertEventBus>(), s);
            return new SiteAlertEvaluators(
                ActivatorUtilities.CreateInstance<MonitoringAlertEvaluator>(_serviceProvider, s, bus),
                ActivatorUtilities.CreateInstance<DeviceHealthAlertEvaluator>(_serviceProvider, s, bus),
                ActivatorUtilities.CreateInstance<SfpAlertEvaluator>(_serviceProvider, s, bus),
                ActivatorUtilities.CreateInstance<CableModemAlertEvaluator>(_serviceProvider, s, bus),
                ActivatorUtilities.CreateInstance<OntAlertEvaluator>(_serviceProvider, s, bus),
                ActivatorUtilities.CreateInstance<CellularAlertEvaluator>(_serviceProvider, s, bus));
        });

    /// <summary>The default site's evaluators.</summary>
    public SiteAlertEvaluators GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
