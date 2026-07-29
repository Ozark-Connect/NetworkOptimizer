using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.RebootReason;

/// <summary>
/// Watches device uptime on every site so reboot reasons resolve on any install that can reach a
/// console.
///
/// The reason itself comes from an SSH probe of the device; the only other thing the feature needs
/// is to notice the device is on a new boot, and the console reports uptime for every device it
/// returns. Neither half involves SNMP, InfluxDB, or metrics collection of any kind.
///
/// It used to be observed only from inside the monitoring collection tiers, several gates deep - no
/// SNMP poller meant an early return, and the API-health fallback that would otherwise have fed it
/// sits after that return. An install with monitoring switched on but nowhere to write metrics, or
/// with no SNMP at all, therefore never recorded a boot and never asked a device why it restarted.
/// This runs on its own and answers to nothing but the console.
/// </summary>
public sealed class DeviceRebootObserver : BackgroundService
{
    /// <summary>Uptime moves slowly; the probe behind it is SSH, so there is no value in hurrying.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a site stays "monitored" after its last monitoring-sourced sample. Comfortably
    /// longer than the tiers' own cadence, so a site that is being polled never flickers between
    /// the two sources.
    /// </summary>
    private static readonly TimeSpan MonitoringSilence = TimeSpan.FromMinutes(20);

    /// <summary>Let the consoles connect before the first pass rather than racing startup.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly SiteConnectionRegistry _connections;
    private readonly DeviceRebootRegistry _trackers;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly ILogger<DeviceRebootObserver> _logger;

    public DeviceRebootObserver(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        SiteConnectionRegistry connections,
        DeviceRebootRegistry trackers,
        Licensing.LicenseStateService licenseState,
        ILogger<DeviceRebootObserver> logger)
    {
        _mainDbFactory = mainDbFactory;
        _connections = connections;
        _trackers = trackers;
        _licenseState = licenseState;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var slug in await OperationalSitesAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    await ObserveSiteAsync(slug, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never fatal, and never one site's problem for another: a console unreachable
                    // this pass is reachable the next, and the only cost is a reason arriving later.
                    _logger.LogDebug(ex, "Device reboot observation failed for site {Site}", slug);
                }
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Every site the install is licensed to collect for - the default one always, plus the enabled
    /// sites when multi-site is on. Mirrors what MonitoringCollectionRegistry considers desired, so
    /// reboot reasons cover exactly the sites monitoring would.
    /// </summary>
    private async Task<List<string>> OperationalSitesAsync(CancellationToken ct)
    {
        var slugs = new List<string>();
        if (_licenseState.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            slugs.Add(SiteManagementService.DefaultSiteSlug);

        try
        {
            await using var db = await _mainDbFactory.CreateDbContextAsync(ct);
            var multiSite = await db.SystemSettings.FindAsync(
                new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
            if (bool.TryParse(multiSite?.Value, out var enabled) && enabled)
            {
                var others = await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled && !s.IsDefault)
                    .Select(s => s.Slug)
                    .ToListAsync(ct);
                slugs.AddRange(others.Where(_licenseState.IsSiteOperational));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate sites for reboot observation");
        }

        return slugs;
    }

    private async Task ObserveSiteAsync(string slug, CancellationToken ct)
    {
        var tracker = _trackers.GetFor(slug);
        var connection = _connections.GetFor(slug);
        if (!connection.IsConnected || connection.Client is null)
            return;

        var devices = await connection.GetDiscoveredDevicesAsync(ct);
        if (devices is null || devices.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var sampled = 0;
        var covered = 0;

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(device.Mac) || device.Uptime.TotalSeconds <= 0)
                continue;

            // Fallback only, and per device. A monitoring tier reads system-stats.uptime; this reads
            // the device's own uptime field, and the two do not always agree - so whichever is
            // already reporting a device keeps it, and this fills in the rest. The tiers skip
            // devices that answer no health data, and those are precisely the ones left here.
            if (tracker.MonitoringIsFeeding(device.Mac, MonitoringSilence))
            {
                covered++;
                continue;
            }

            sampled++;

            // The tracker decides what is new and paces its own probing; this only supplies the
            // signal it needs to decide.
            tracker.RecordUptimeSample(
                device.Mac,
                device.Name,
                device.Type,
                device.DisplayIpAddress,
                (long)device.Uptime.TotalSeconds,
                device.Firmware,
                now,
                DeviceRebootTracker.UptimeSource.Console);
        }

        // Says the pass happened even when it changes nothing, which is the normal case: without it
        // there is no way to tell "observed, nothing new" from "never ran". What each sample leads
        // to - a first sighting, a detected reboot, a probe result - the tracker logs itself.
        _logger.LogDebug(
            "Reboot observation: site {Site}, {Sampled} of {Total} devices sampled "
            + "({Covered} already fed by monitoring)",
            slug, sampled, devices.Count, covered);
    }
}
