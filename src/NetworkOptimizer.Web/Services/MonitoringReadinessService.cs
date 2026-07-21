using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Central readiness checks for the monitoring subsystem, shared by the Monitoring page
/// and by features that deep-link into it (e.g. a speed-test result linking into Live
/// View historic playback). Keeps the "is monitoring usable for this site" rule in one
/// place so callers don't re-derive it and drift.
/// </summary>
public class MonitoringReadinessService
{
    private readonly SnmpDetectionService _snmp;
    private readonly SiteContextService _siteContext;
    private readonly SiteDbContextFactory _siteDb;

    public MonitoringReadinessService(
        SnmpDetectionService snmp,
        SiteContextService siteContext,
        SiteDbContextFactory siteDb)
    {
        _snmp = snmp;
        _siteContext = siteContext;
        _siteDb = siteDb;
    }

    /// <summary>
    /// Whether the shared InfluxDB connection is configured. InfluxDB is a single central
    /// instance whose token/url live on the default site; secondary (managed) sites inherit
    /// them and carry no token in their own <see cref="Storage.Models.MonitoringSettings"/>
    /// row. This always reads the default site's row, so it answers the same regardless of
    /// which site is current.
    /// </summary>
    public async Task<bool> IsSharedInfluxConfiguredAsync(CancellationToken ct = default)
    {
        try
        {
            await using var mainDb = _siteDb.CreateForSite(SiteManagementService.DefaultSiteSlug, isDefault: true);
            var shared = await mainDb.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            return !string.IsNullOrEmpty(shared?.InfluxDbToken)
                && !string.IsNullOrEmpty(shared?.InfluxDbUrl);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether Live View historic playback is available for the CURRENT site, so a
    /// speed-test result can offer a deep link into it: monitoring must be enabled for this
    /// site AND the shared InfluxDB connection must be configured. Mirrors the Monitoring
    /// page's own readiness gate (<c>_monitoringEnabled &amp;&amp; _influxConfigured</c>).
    /// </summary>
    public async Task<bool> IsHistoricPlaybackAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = await _snmp.GetOrCreateSettingsAsync(ct);
            return settings.Enabled && await IsSharedInfluxConfiguredAsync(ct);
        }
        catch
        {
            return false;
        }
    }
}
