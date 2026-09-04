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
    /// <summary>The site has an enrolled agent that is not connected right now (agent restart,
    /// server startup, tunnel bounce). Nothing is shown: what that agent is and can do is
    /// unknowable until its hello, and pitching "install one" at a site that has one is noise.</summary>
    AwaitingAgent,
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
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;

    public ConntrackStatusService(
        AgentTunnelRegistry tunnelRegistry,
        AgentOnGatewayDetector onGatewayDetector,
        SiteContextService siteContext,
        SiteDbContextFactory siteDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory)
    {
        _tunnelRegistry = tunnelRegistry;
        _onGatewayDetector = onGatewayDetector;
        _siteContext = siteContext;
        _siteDbFactory = siteDbFactory;
        _mainDbFactory = mainDbFactory;
    }

    /// <inheritdoc />
    public async Task<GatewayAgentConntrackStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var needsUpdate = false;
        var connections = _tunnelRegistry.GetForSite(_siteContext.Slug);
        foreach (var connection in connections)
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

        // An enrolled agent that is not connected right now makes the site unanswerable: it may
        // BE the gateway agent, mid-restart. Hold the suggestion until every enrolled agent has
        // said hello - and hold it too when enrollment cannot be read, since a wrongly flashed
        // install pitch is the failure mode this state exists to prevent.
        try
        {
            await using var mainDb = await _mainDbFactory.CreateDbContextAsync(ct);
            var enrolledIds = await mainDb.Sites.AsNoTracking()
                .Where(s => s.Slug == _siteContext.Slug)
                .Join(mainDb.SiteAgents.AsNoTracking(), s => s.Id, a => a.SiteId, (s, a) => a)
                .Where(a => a.Enabled && a.EnrolledAt != null)
                .Select(a => a.Id)
                .ToListAsync(ct);
            var connectedIds = connections.Select(c => c.AgentId).ToHashSet();
            if (enrolledIds.Any(id => !connectedIds.Contains(id)))
                return new GatewayAgentConntrackStatus(GatewayAgentConntrackState.AwaitingAgent, false);
        }
        catch
        {
            return new GatewayAgentConntrackStatus(GatewayAgentConntrackState.AwaitingAgent, false);
        }

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
