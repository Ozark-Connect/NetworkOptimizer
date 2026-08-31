using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;

/// <summary>What the Bandwidth Hogs card should say about the gateway conntrack feed.</summary>
public enum GatewayAgentConntrackState
{
    /// <summary>A gateway agent with the conntrack capability is connected: nothing to show.</summary>
    Covered,
    /// <summary>An agent IS on the gateway but its build predates conntrack accounting: show the
    /// update notification. Keyed off capability absence, never a version constant, so it
    /// self-clears on the first capable hello.</summary>
    GatewayAgentNeedsUpdate,
    /// <summary>No agent runs on this site's gateway: offer the suggestion (dismissible).</summary>
    NoGatewayAgent,
}

public sealed record GatewayAgentConntrackStatus(GatewayAgentConntrackState State, bool SuggestionDismissed);

/// <summary>
/// Answers the Bandwidth Hogs card's gateway-agent status, and records the suggestion dismissal.
///
/// Reads are Viewer - the card is on Live View, open to any role. The dismissal is Admin and
/// stored PER SITE in the site DB's SystemSettings (never AdminSettings, the install-wide single
/// row); the card only offers it to Site Admins, who are the ones who can act on the suggestion.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IConntrackStatusService
{
    [RequireRole(Roles.Viewer)]
    Task<GatewayAgentConntrackStatus> GetStatusAsync(CancellationToken ct = default);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "bandwidth_hogs")]
    Task DismissGatewayAgentSuggestionAsync();
}

/// <inheritdoc cref="IConntrackStatusService" />
public class ConntrackStatusService : IConntrackStatusService
{
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly AgentOnGatewayDetector _onGatewayDetector;
    private readonly SiteContextService _siteContext;
    private readonly SiteDbContextFactory _siteDbFactory;

    public ConntrackStatusService(
        AgentTunnelRegistry tunnelRegistry,
        AgentOnGatewayDetector onGatewayDetector,
        SiteContextService siteContext,
        SiteDbContextFactory siteDbFactory)
    {
        _tunnelRegistry = tunnelRegistry;
        _onGatewayDetector = onGatewayDetector;
        _siteContext = siteContext;
        _siteDbFactory = siteDbFactory;
    }

    /// <inheritdoc />
    public async Task<GatewayAgentConntrackStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var needsUpdate = false;
        foreach (var connection in _tunnelRegistry.GetForSite(_siteContext.Slug))
        {
            // The hello's reported flag when the agent carries one; the IP-correlation detector
            // for the pre-flag installs - an OLD agent sitting on a gateway must still read as a
            // gateway agent here, or it would be offered as missing rather than as updatable.
            var onGateway = connection.OnGateway
                ?? await _onGatewayDetector.IsAgentOnGatewayAsync(
                    _siteContext.Slug, connection.AgentId, connection.HostAddresses, ct);
            if (!onGateway) continue;
            if (connection.HasCapability(AgentTunnelConnection.ConntrackCapability))
                return new GatewayAgentConntrackStatus(GatewayAgentConntrackState.Covered, false);
            needsUpdate = true;
        }
        if (needsUpdate)
            return new GatewayAgentConntrackStatus(GatewayAgentConntrackState.GatewayAgentNeedsUpdate, false);

        var dismissed = false;
        try
        {
            await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
            dismissed = (await db.SystemSettings.FindAsync(
                new object[] { SystemSettingKeys.BandwidthHogsGatewayAgentSuggestedDismissed }, ct))?.Value == "true";
        }
        catch
        {
            // Unreadable settings leave the suggestion visible; it is dismissible either way.
        }
        return new GatewayAgentConntrackStatus(GatewayAgentConntrackState.NoGatewayAgent, dismissed);
    }

    /// <inheritdoc />
    public async Task DismissGatewayAgentSuggestionAsync()
    {
        await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var setting = await db.SystemSettings.FindAsync(SystemSettingKeys.BandwidthHogsGatewayAgentSuggestedDismissed);
        if (setting == null)
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SystemSettingKeys.BandwidthHogsGatewayAgentSuggestedDismissed,
                Value = "true"
            });
        else
            setting.Value = "true";
        await db.SaveChangesAsync();
    }
}
