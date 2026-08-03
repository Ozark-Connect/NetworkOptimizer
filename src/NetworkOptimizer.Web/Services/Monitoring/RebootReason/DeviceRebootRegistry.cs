using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring.RebootReason;

/// <summary>
/// Per-site device reboot trackers. The collection tier feeds the site's tracker with uptime
/// samples; pages read the same instance for the reason behind a device's current boot. Same
/// pattern as MonitoringLiveStatsRegistry: instances are in-memory caches, nothing to dispose.
/// </summary>
public class DeviceRebootRegistry : ISiteScopedRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiSshRegistry _deviceSshRegistry;
    private readonly GatewaySshRegistry _gatewaySshRegistry;
    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly MonitoringAlertRegistry _alertRegistry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, DeviceRebootTracker> _instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the registry.</summary>
    public DeviceRebootRegistry(
        IServiceProvider serviceProvider,
        UniFiSshRegistry deviceSshRegistry,
        GatewaySshRegistry gatewaySshRegistry,
        MonitoringInfluxRegistry influxRegistry,
        MonitoringAlertRegistry alertRegistry,
        ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _deviceSshRegistry = deviceSshRegistry;
        _gatewaySshRegistry = gatewaySshRegistry;
        _influxRegistry = influxRegistry;
        _alertRegistry = alertRegistry;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The reboot tracker for a site, created on first use.</summary>
    public DeviceRebootTracker GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            var probe = new DeviceRebootProbe(
                _deviceSshRegistry.GetFor(s),
                _gatewaySshRegistry.GetFor(s),
                _loggerFactory.CreateLogger<DeviceRebootProbe>());

            return new DeviceRebootTracker(
                probe,
                _influxRegistry.GetFor(s),
                _alertRegistry.GetFor(s).DeviceReboot,
                _loggerFactory.CreateLogger<DeviceRebootTracker>());
        });

    /// <summary>The default site's tracker.</summary>
    public DeviceRebootTracker GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _instances.TryRemove(slug, out _);
        return null;
    }
}
