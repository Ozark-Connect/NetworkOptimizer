using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// Whether a WAN is quiet enough to take a learning sample on. Reads the same series Live View and
/// ISP Health show: the gateway's SNMP <c>interface_counters</c> for the WAN's counter interface,
/// resolved live from the console and, when the console is down, from the WAN's remembered profile.
/// The counter interface is not always the data-path interface (a VLAN or PPPoE WAN counts on
/// the physical port), which is why this never reads the data-path name straight.
/// </summary>
public class WanIdleGate
{
    /// <summary>Below this, in both directions, the link counts as idle.</summary>
    public const double IdleThresholdMbps = 1.0;

    /// <summary>How far back a reading may be and still describe "now"; matches the live tiles' 90 s.</summary>
    public static readonly TimeSpan MaxReadingAge = TimeSpan.FromSeconds(90);

    /// <summary>Window the max is taken over: a burst anywhere in the last minute means not idle.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly UniFiConnectionService _connectionService;
    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<WanIdleGate> _logger;

    // The registry rather than the scoped MonitoringInfluxClient forwarder: the schedule executor
    // runs in a synchronously disposed scope, and the forwarder registers an async-only disposable
    // there, which the container refuses to dispose ("type only implements IAsyncDisposable").
    public WanIdleGate(
        UniFiConnectionService connectionService,
        MonitoringInfluxRegistry influxRegistry,
        SiteDbContextFactory siteDb,
        SiteContextService siteContext,
        ILogger<WanIdleGate> logger)
    {
        _connectionService = connectionService;
        _influxRegistry = influxRegistry;
        _siteDb = siteDb;
        _siteContext = siteContext;
        _logger = logger;
    }

    private MonitoringInfluxClient Influx => _influxRegistry.GetFor(_siteContext.Slug);

    /// <summary>A traffic reading for the WAN and where it came from.</summary>
    public sealed record IdleReading(double DownloadMbps, double UploadMbps, string Source)
    {
        public bool IsIdle => DownloadMbps <= IdleThresholdMbps && UploadMbps <= IdleThresholdMbps;
    }

    /// <summary>
    /// Peak SNMP throughput on the WAN over the last minute, or null when monitoring is off, the
    /// WAN's counter interface cannot be resolved, or nothing fresh has been recorded - the caller
    /// then falls back to reading the gateway directly.
    /// </summary>
    public async Task<IdleReading?> ReadMonitoredAsync(string wanNetworkGroup, CancellationToken ct)
    {
        try
        {
            await using (var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault))
            {
                var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (settings is not { Enabled: true })
                    return null;
            }

            var (mac, counter) = await ResolveCounterAsync(wanNetworkGroup, ct);
            if (string.IsNullOrEmpty(mac) || string.IsNullOrEmpty(counter))
                return null;

            var now = DateTime.UtcNow;
            var points = await Influx.QueryGatewayWanRatesAsync(
                mac, new[] { counter }, now - MaxReadingAge, now,
                aggregateWindow: TimeSpan.FromSeconds(5), ct: ct);
            var fresh = points.Where(p => p.Time >= now - Window).ToList();
            if (fresh.Count == 0)
                return null;

            var down = fresh.Max(p => p.DownloadBps ?? 0) / 1_000_000.0;
            var up = fresh.Max(p => p.UploadBps ?? 0) / 1_000_000.0;
            return new IdleReading(down, up, "snmp");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read monitored WAN traffic for {Group}; falling back to the gateway counters", wanNetworkGroup);
            return null;
        }
    }

    /// <summary>
    /// Gateway MAC and counter interface for the WAN group: live from the console, else the WAN's
    /// remembered profile row. Never another WAN's - an unresolved WAN reads as unknown.
    /// </summary>
    private async Task<(string? Mac, string? Counter)> ResolveCounterAsync(string wanNetworkGroup, CancellationToken ct)
    {
        string? mac = null;
        string? counter = null;
        try
        {
            var devices = await _connectionService.GetDiscoveredDevicesAsync(ct);
            mac = devices?.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway)?.Mac;
            var ifaces = await _connectionService.GetWanInterfacesForGroupAsync(wanNetworkGroup, ct);
            counter = ifaces?.CounterIfName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Console did not resolve WAN {Group}'s counter interface", wanNetworkGroup);
        }

        if (string.IsNullOrEmpty(mac) || string.IsNullOrEmpty(counter))
        {
            await using var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
            var profile = await db.WanProfiles.AsNoTracking()
                .Where(w => w.WanNetworkgroup == wanNetworkGroup)
                .OrderByDescending(w => w.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            counter ??= profile?.CounterInterface;
            if (string.IsNullOrEmpty(mac) && profile?.GatewayMac != null)
                mac = profile.GatewayMac.Replace("-", ":").ToLowerInvariant();
        }

        return (mac, counter);
    }
}
