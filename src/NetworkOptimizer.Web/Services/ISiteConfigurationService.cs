using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>The per-site settings the Multi-Site table edits, read in one pass.</summary>
public sealed record SiteConfiguration(bool ConsoleViaAgent, bool DevicesViaAgent, bool AgentCoversSite, string? ClientSpeedTestTarget);

/// <summary>
/// The tunnel routing and speed-test target a site runs with. These were written straight from the
/// Multi-Site table against whichever site's database the row named, which left nothing at all
/// deciding who could change them - and the row is any site in the table, so being Site Admin of one
/// site was enough to reconfigure another.
///
/// Every method authorizes against the slug it is given rather than the site in context, because the
/// caller is editing a row, not the site they are looking at. Changing how a site is reached is Site
/// Admin: it is not repeated as part of running the network, and getting it wrong takes the console
/// or the devices out of reach until someone puts it back.
/// </summary>
[MutatingService]
public interface ISiteConfigurationService
{
    /// <summary>What one site is configured with. Viewer-level: the table shows these as state.</summary>
    [RequireSiteRole(SiteRole.SiteViewer)]
    Task<SiteConfiguration> GetAsync([SiteSlug] string siteSlug);

    /// <summary>Routes this site's UniFi Console through its agent tunnel.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SiteChanged, TargetType = "site")]
    Task SetConsoleViaAgentAsync([SiteSlug] string siteSlug, bool enabled);

    /// <summary>Routes this site's device access (SSH, modem and ONT pages) through its agent tunnel.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SiteChanged, TargetType = "site")]
    Task SetDevicesViaAgentAsync([SiteSlug] string siteSlug, bool enabled);

    /// <summary>
    /// Hands collection for this site to its on-site agent, standing this server down. Only
    /// meaningful for the default site - a secondary site's agent already collects - and only takes
    /// effect while an agent is actually enrolled.
    /// </summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SiteChanged, TargetType = "site")]
    Task SetAgentCoversSiteAsync([SiteSlug] string siteSlug, bool enabled);

    /// <summary>Overrides where browsers reach this site's speed test pages. Null clears it.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.SiteChanged, TargetType = "site")]
    Task SetClientSpeedTestTargetAsync([SiteSlug] string siteSlug, string? target);
}

/// <inheritdoc />
public sealed class SiteConfigurationService : ISiteConfigurationService
{
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteAgentCoverage _agentCoverage;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly ILogger<SiteConfigurationService> _logger;

    public SiteConfigurationService(SiteDbContextFactory siteDb, SiteAgentCoverage agentCoverage,
        SiteConnectionRegistry siteConnections, ILogger<SiteConfigurationService> logger)
    {
        _siteDb = siteDb;
        _agentCoverage = agentCoverage;
        _siteConnections = siteConnections;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SiteConfiguration> GetAsync(string siteSlug)
    {
        await using var db = OpenSite(siteSlug);
        var settings = await db.SystemSettings
            .Where(s => s.Key == UniFiConnectionService.ConsoleViaAgentKey
                     || s.Key == SiteTunnelRouting.DevicesViaAgentKey
                     || s.Key == SiteAgentCoverage.AgentCoversSiteKey
                     || s.Key == SystemSettingKeys.ClientSpeedTestTargetOverride)
            .ToListAsync();

        return new SiteConfiguration(
            ReadFlag(settings, UniFiConnectionService.ConsoleViaAgentKey),
            ReadFlag(settings, SiteTunnelRouting.DevicesViaAgentKey),
            ReadFlag(settings, SiteAgentCoverage.AgentCoversSiteKey),
            settings.FirstOrDefault(s => s.Key == SystemSettingKeys.ClientSpeedTestTargetOverride)?.Value);
    }

    /// <inheritdoc />
    public Task SetConsoleViaAgentAsync(string siteSlug, bool enabled)
        => WriteAsync(siteSlug, UniFiConnectionService.ConsoleViaAgentKey, enabled.ToString());

    /// <inheritdoc />
    public Task SetDevicesViaAgentAsync(string siteSlug, bool enabled)
        => WriteAsync(siteSlug, SiteTunnelRouting.DevicesViaAgentKey, enabled.ToString());

    /// <inheritdoc />
    public async Task SetAgentCoversSiteAsync(string siteSlug, bool enabled)
    {
        await WriteAsync(siteSlug, SiteAgentCoverage.AgentCoversSiteKey, enabled.ToString());
        // The collection paths read this through a one-minute cache; a setting that decides whether
        // the server collects at all should not wait that long to take effect.
        _agentCoverage.Invalidate(siteSlug);

        // The console is reached by whichever path was chosen when its client was built, so the
        // existing one is now on the wrong side of this switch. Nothing else re-establishes it:
        // every automatic reconnect is gated on the console being disconnected, and a console
        // parked in awaiting-agent stays parked. Not awaited - a reconnect can take seconds and
        // this runs from a checkbox.
        var connection = _siteConnections.GetFor(siteSlug);
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.ReconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not reconnect the console for site {Slug} after its agent coverage changed", siteSlug);
            }
        });
    }

    /// <inheritdoc />
    public Task SetClientSpeedTestTargetAsync(string siteSlug, string? target)
    {
        var trimmed = target?.Trim();
        return WriteAsync(siteSlug, SystemSettingKeys.ClientSpeedTestTargetOverride,
            string.IsNullOrEmpty(trimmed) ? null : trimmed);
    }

    private async Task WriteAsync(string siteSlug, string key, string? value)
    {
        await using var db = OpenSite(siteSlug);
        var setting = await db.SystemSettings.FindAsync(key);
        if (setting is null)
        {
            if (value is null)
                return;
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The slug names the database, so it is resolved here rather than taken from the caller: a
    /// caller that could pass "this is the default site" alongside another site's slug would write
    /// to a database the gate above never authorized.
    /// </summary>
    private NetworkOptimizerDbContext OpenSite(string siteSlug)
        => _siteDb.CreateForSite(siteSlug, siteSlug == SiteManagementService.DefaultSiteSlug);

    private static bool ReadFlag(List<SystemSetting> settings, string key)
        => bool.TryParse(settings.FirstOrDefault(s => s.Key == key)?.Value, out var enabled) && enabled;
}
