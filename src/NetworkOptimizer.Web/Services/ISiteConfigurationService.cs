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
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly ILogger<SiteConfigurationService> _logger;

    public SiteConfigurationService(SiteDbContextFactory siteDb, SiteAgentCoverage agentCoverage,
        SiteConnectionRegistry siteConnections, SiteTunnelRouting tunnelRouting,
        ILogger<SiteConfigurationService> logger)
    {
        _siteDb = siteDb;
        _agentCoverage = agentCoverage;
        _siteConnections = siteConnections;
        _tunnelRouting = tunnelRouting;
        _logger = logger;
    }

    /// <summary>
    /// Rebuilds the site's console on whichever path it should now take. The client records how it
    /// was built, so a setting that changes the path leaves the existing connection on the old one
    /// until something reconnects it - and nothing else does, because every automatic reconnect is
    /// gated on the console being disconnected. Not awaited: a reconnect takes seconds and every
    /// caller here is a checkbox.
    /// </summary>
    private void ReconnectConsole(string siteSlug, string because)
    {
        var connection = _siteConnections.GetFor(siteSlug);
        _ = Task.Run(async () =>
        {
            try
            {
                await connection.ReconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reconnect the console for site {Slug} after {Because}",
                    siteSlug, because);
            }
        });
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
    public async Task SetConsoleViaAgentAsync(string siteSlug, bool enabled)
    {
        await WriteAsync(siteSlug, UniFiConnectionService.ConsoleViaAgentKey, enabled.ToString());
        // This checkbox only appears once coverage is on, so coverage is necessarily switched
        // first: reconnecting there alone always ran against the console's OLD routing and left
        // this choice unapplied until something else happened to reconnect.
        ReconnectConsole(siteSlug, "its console routing changed");
    }

    /// <inheritdoc />
    public async Task SetDevicesViaAgentAsync(string siteSlug, bool enabled)
    {
        await WriteAsync(siteSlug, SiteTunnelRouting.DevicesViaAgentKey, enabled.ToString());
        // Consulted per SSH command and per modem poll through a one-minute cache, so without this
        // the switch appears to do nothing for up to a minute.
        _tunnelRouting.Invalidate(siteSlug);
    }

    /// <inheritdoc />
    public async Task SetAgentCoversSiteAsync(string siteSlug, bool enabled)
    {
        await WriteAsync(siteSlug, SiteAgentCoverage.AgentCoversSiteKey, enabled.ToString());
        // The collection paths read this through a one-minute cache; a setting that decides whether
        // the server collects at all should not wait that long to take effect.
        // Recorded rather than invalidated: the reconnect below reads this immediately, and the
        // synchronous reader answers false while an invalidated entry refills.
        _agentCoverage.Set(siteSlug, enabled);
        // Also drops the devices cache: that flag is gated on coverage for the default site, so
        // coverage changing changes the answer without the flag itself being touched.
        _tunnelRouting.Invalidate(siteSlug);
        ReconnectConsole(siteSlug, "its agent coverage changed");
    }

    /// <inheritdoc />
    public Task SetClientSpeedTestTargetAsync(string siteSlug, string? target)
    {
        var trimmed = target?.Trim();
        // The value ends up inside a JavaScript string on the client pages, so its shape is checked
        // here rather than trusted there.
        if (!string.IsNullOrEmpty(trimmed) && !NetworkOptimizer.Core.Helpers.UrlSafety.IsSafeHostOrHttpUrl(trimmed))
            throw new ArgumentException("Enter a full http(s) URL or a bare host, with no spaces or quotes.");
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
