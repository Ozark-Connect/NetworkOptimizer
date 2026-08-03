using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages on-site agent registration: one-time enrollment tokens, token-to-key
/// exchange, and heartbeats. Agent rows are registry data, so all access goes
/// through the main-database factory. Raw tokens and keys are returned exactly
/// once; only SHA-256 hashes are stored.
/// </summary>
public class AgentEnrollmentService : IAgentEnrollmentService
{
    /// <summary>Agents reporting within this window count as online.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    /// <summary>Unused enrollment tokens stop working after this long.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private const string TokenPrefix = "noa_";
    private const string KeyPrefix = "noak_";

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly SiteAgentCoverage _agentCoverage;
    private readonly IServiceProvider _serviceProvider;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly ILogger<AgentEnrollmentService> _logger;
    private readonly Authorization.ISiteAccessFilter _siteAccess;

    public AgentEnrollmentService(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        AgentTunnelRegistry tunnelRegistry,
        Authorization.ISiteAccessFilter siteAccess,
        SiteAgentCoverage agentCoverage,
        IServiceProvider serviceProvider,
        SiteTunnelRouting tunnelRouting,
        ILogger<AgentEnrollmentService> logger)
    {
        _siteAccess = siteAccess;
        _mainDbFactory = mainDbFactory;
        _tunnelRegistry = tunnelRegistry;
        _agentCoverage = agentCoverage;
        _serviceProvider = serviceProvider;
        _tunnelRouting = tunnelRouting;
        _logger = logger;
    }

    /// <summary>
    /// Clears the console and device tunnel routing flags for a site. Both name a tunnel, so once
    /// the site has no agent they can only point at something that will never answer.
    /// </summary>
    private async Task ClearAgentRoutingAsync(string siteSlug)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            foreach (var key in new[] { UniFiConnectionService.ConsoleViaAgentKey, SiteTunnelRouting.DevicesViaAgentKey })
            {
                var setting = await db.SystemSettings.FindAsync(key);
                if (setting == null || !bool.TryParse(setting.Value, out var on) || !on) continue;
                setting.Value = bool.FalseString;
            }
            await db.SaveChangesAsync();
            _tunnelRouting.Invalidate(siteSlug);
            _logger.LogInformation("Cleared agent routing for site {Slug} - its last agent was removed", siteSlug);
        }
        catch (Exception ex)
        {
            // The agent is already gone; failing to tidy the flags must not fail the removal.
            _logger.LogWarning(ex, "Could not clear agent routing flags for site {Slug}", siteSlug);
        }
    }

    /// <summary>Agents registered for a site, newest first.</summary>
    public async Task<List<SiteAgent>> GetAgentsForSiteAsync(int siteId)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        // The site id arrives from the caller, so authorization for it is asked here rather than
        // assumed from whatever page supplied it.
        var slug = await db.Sites.Where(x => x.Id == siteId).Select(x => x.Slug).FirstOrDefaultAsync();
        if (slug is not null && !await _siteAccess.IsAuthorizedAsync(slug))
            return new List<SiteAgent>();

        return await db.SiteAgents
            .Where(a => a.SiteId == siteId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    /// <summary>All agents across sites (for the overview card).</summary>
    public async Task<List<SiteAgent>> GetAllAgentsAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agents = await db.SiteAgents.ToListAsync();

        // Agent rows name the sites they serve, so the same narrowing the site list gets applies
        // here. Background scopes are unfiltered, as always.
        var slugsById = await db.Sites.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Slug);
        return await _siteAccess.FilterAsync(agents, a => slugsById.GetValueOrDefault(a.SiteId) ?? "");
    }

    /// <summary>
    /// Registers a new agent for a site and returns its one-time enrollment
    /// token. The token is not retrievable afterwards.
    /// </summary>
    public async Task<(SiteAgent Agent, string Token)> CreateAgentAsync(string siteSlug, string name)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();

        // The slug is what was authorized, so it is also what decides the site the agent lands on.
        var siteId = await db.Sites.Where(x => x.Slug == siteSlug).Select(x => x.Id).FirstOrDefaultAsync();
        if (siteId == 0)
            throw new InvalidOperationException($"No site with slug '{siteSlug}'.");

        var token = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var agent = new SiteAgent
        {
            SiteId = siteId,
            Name = string.IsNullOrWhiteSpace(name) ? "Agent" : name.Trim(),
            EnrollmentTokenHash = Hash(token),
            TokenCreatedAt = DateTime.UtcNow,
        };

        db.SiteAgents.Add(agent);
        await db.SaveChangesAsync();
        _logger.LogInformation("Created agent {Name} (id {Id}) for site {SiteId}", agent.Name, agent.Id, siteId);
        return (agent, token);
    }

    /// <summary>
    /// Closes an agent's tunnel from this end. The key is only checked when the stream opens, so a
    /// removed or disabled agent whose connection is already up keeps relaying monitoring, SSH and
    /// console traffic until something unrelated interrupts it - a restart, or a network blip. That
    /// is not a revocation. The agent sees its stream close and retries on its own backoff; the key
    /// no longer resolves, so the reconnect is refused. Same mechanism license enforcement uses.
    /// </summary>
    private void DropLiveTunnel(int agentId, string agentName, string reason)
    {
        foreach (var connection in _tunnelRegistry.GetAll().Where(c => c.AgentId == agentId))
        {
            _logger.LogInformation("Dropping tunnel for agent {Name} (id {Id}): {Reason}",
                agentName, agentId, reason);
            connection.Drop();
        }
    }

    /// <summary>
    /// The agent, but only if it belongs to the site the caller was authorized against. Everything
    /// that acts on an agent id goes through here: the id is the caller's to choose, the slug is the
    /// one the gate checked, and an agent that does not sit on that slug is not theirs to touch.
    /// </summary>
    private static async Task<SiteAgent?> AgentOnSiteAsync(
        NetworkOptimizerDbContext db, string siteSlug, int agentId)
    {
        var agent = await db.SiteAgents.FindAsync(agentId);
        if (agent is null)
            return null;

        var slug = await db.Sites.Where(x => x.Id == agent.SiteId).Select(x => x.Slug).FirstOrDefaultAsync();
        return string.Equals(slug, siteSlug, StringComparison.OrdinalIgnoreCase) ? agent : null;
    }

    /// <summary>
    /// Issues a fresh one-time enrollment token for an existing agent that has
    /// not enrolled yet, so re-entering setup for a site reuses the same agent
    /// row instead of piling up duplicates. Returns null if the agent is gone or
    /// already enrolled.
    /// </summary>
    public async Task<string?> ReissueTokenAsync(string siteSlug, int agentId)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await AgentOnSiteAsync(db, siteSlug, agentId);
        if (agent == null || agent.EnrolledAt != null)
            return null;

        var token = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        agent.EnrollmentTokenHash = Hash(token);
        agent.TokenCreatedAt = DateTime.UtcNow;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _logger.LogInformation("Reissued enrollment token for agent {Name} (id {Id})", agent.Name, agent.Id);
        return token;
    }

    /// <summary>Removes an agent registration entirely (its token/key stop working).</summary>
    public async Task DeleteAgentAsync(string siteSlug, int agentId)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await AgentOnSiteAsync(db, siteSlug, agentId);
        if (agent == null)
            return;

        db.SiteAgents.Remove(agent);
        await db.SaveChangesAsync();
        DropLiveTunnel(agent.Id, agent.Name, "removed");
        _logger.LogInformation("Removed agent {Name} (id {Id}) for site {SiteId}", agent.Name, agent.Id, agent.SiteId);

        // Removing the last agent leaves nothing to route through, so the console and device
        // routing flags are cleared with it. They outlived the agent otherwise, and every console
        // read and SSH command went on addressing a tunnel that could never come up again.
        if (!await db.SiteAgents.AnyAsync(a => a.SiteId == agent.SiteId))
            await ClearAgentRoutingAsync(siteSlug);
    }

    /// <summary>
    /// Exchanges a one-time enrollment token for a long-lived agent key.
    /// Returns the raw key and the site slug the agent should operate under.
    /// </summary>
    public async Task<(bool Success, string? AgentKey, string? SiteSlug, string? Error)> EnrollAsync(string token, string? version, string? lanIp = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, null, "Missing enrollment token");

        var tokenHash = Hash(token.Trim());
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await db.SiteAgents.FirstOrDefaultAsync(a => a.EnrollmentTokenHash == tokenHash);
        if (agent == null || !agent.Enabled)
            return (false, null, null, "Invalid enrollment token");
        if (agent.EnrolledAt != null)
            return (false, null, null, "Enrollment token already used");
        if (agent.TokenCreatedAt == null || DateTime.UtcNow - agent.TokenCreatedAt > TokenLifetime)
            return (false, null, null, "Enrollment token expired - generate a new one");

        var site = await db.Sites.FindAsync(agent.SiteId);
        if (site == null)
            return (false, null, null, "Agent's site no longer exists");

        var key = KeyPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        agent.AgentKeyHash = Hash(key);
        agent.EnrolledAt = DateTime.UtcNow;
        agent.LastSeenAt = DateTime.UtcNow;
        agent.LastVersion = Truncate(version, 32);
        var normalizedLanIp = NormalizeLanIp(lanIp);
        if (normalizedLanIp != null)
            agent.LanIp = normalizedLanIp;
        agent.EnrollmentTokenHash = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // The startup seed skips On-Site Agent alert rules while the default site has
        // no agent; the first default-site enrollment is the moment they become relevant.
        if (site.Slug == SiteManagementService.DefaultSiteSlug)
        {
            var agentRuleDefaults = NetworkOptimizer.Alerts.DefaultAlertRules.GetDefaults()
                .Where(r => r.Source == "agent")
                .ToList();
            var existingPatterns = await db.AlertRules
                .Where(r => r.Source == "agent")
                .Select(r => r.EventTypePattern)
                .ToListAsync();
            var missingRules = agentRuleDefaults.Where(d => !existingPatterns.Contains(d.EventTypePattern)).ToList();
            if (missingRules.Count > 0)
            {
                db.AlertRules.AddRange(missingRules);
                await db.SaveChangesAsync();
                _logger.LogInformation("Seeded {Count} On-Site Agent alert rule(s) for the default site", missingRules.Count);
            }
        }

        _logger.LogInformation("Agent {Name} (id {Id}) enrolled for site {Slug}", agent.Name, agent.Id, site.Slug);
        return (true, key, site.Slug, null);
    }

    /// <summary>
    /// Resolves an agent key to its enabled agent and site slug. Used by the
    /// tunnel to authenticate the first message on a new connection.
    /// </summary>
    public async Task<(SiteAgent Agent, string SiteSlug)?> AuthenticateByKeyAsync(string agentKey)
    {
        if (string.IsNullOrWhiteSpace(agentKey))
            return null;

        var keyHash = Hash(agentKey.Trim());
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await db.SiteAgents.AsNoTracking().FirstOrDefaultAsync(a => a.AgentKeyHash == keyHash);
        if (agent == null || !agent.Enabled)
            return null;

        var site = await db.Sites.FindAsync(agent.SiteId);
        return site == null ? null : (agent, site.Slug);
    }

    /// <summary>Records a heartbeat for an enrolled agent, keyed by its agent key.</summary>
    public async Task<bool> HeartbeatAsync(string agentKey, string? version, string? lanIp = null)
    {
        if (string.IsNullOrWhiteSpace(agentKey))
            return false;

        var keyHash = Hash(agentKey.Trim());
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await db.SiteAgents.FirstOrDefaultAsync(a => a.AgentKeyHash == keyHash);
        if (agent == null || !agent.Enabled)
            return false;

        agent.LastSeenAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(version))
            agent.LastVersion = Truncate(version, 32);
        var normalizedLanIp = NormalizeLanIp(lanIp);
        if (normalizedLanIp != null)
            agent.LanIp = normalizedLanIp;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// The LAN IP of an enrolled, enabled, online agent for the given site slug,
    /// or null when the site has no such agent (no agent, agent offline, or its LAN IP is not yet
    /// known). Used to point site clients at the on-site agent for LAN speed tests. The default
    /// site answers null unless it is configured for its agent to cover it - otherwise clients
    /// would be sent to an agent for a network this server is already on.
    /// </summary>
    public async Task<string?> GetOnlineAgentLanIpAsync(string siteSlug)
    {
        if (string.IsNullOrWhiteSpace(siteSlug))
            return null;
        if (siteSlug == SiteManagementService.DefaultSiteSlug && !_agentCoverage.Covers(siteSlug))
            return null;

        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == siteSlug);
        if (site == null)
            return null;

        // Filter to reachable agents FIRST (open tunnel, or a fresh REST heartbeat
        // - the LAN speed test hits the agent's nginx directly, not the tunnel, so
        // a heartbeat-only agent is still a valid target even when its tunnel is
        // down), then take the most recently seen, so a site with one stale and one
        // reachable agent still resolves.
        var agents = await db.SiteAgents.AsNoTracking()
            .Where(a => a.SiteId == site.Id && a.Enabled && a.EnrolledAt != null && a.LanIp != null)
            .OrderByDescending(a => a.LastSeenAt)
            .ToListAsync();

        return agents.FirstOrDefault(a => _tunnelRegistry.IsReachable(a))?.LanIp;
    }

    /// <summary>Enables or disables an agent. Disabled agents cannot enroll or heartbeat.</summary>
    public async Task SetEnabledAsync(string siteSlug, int agentId, bool enabled)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await AgentOnSiteAsync(db, siteSlug, agentId);
        if (agent == null)
            return;

        agent.Enabled = enabled;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        if (!enabled)
            DropLiveTunnel(agent.Id, agent.Name, "disabled");
    }

    /// <summary>Whether a last-seen timestamp counts as online right now.</summary>
    public static bool IsOnline(DateTime? lastSeenAt) =>
        lastSeenAt != null && DateTime.UtcNow - lastSeenAt < OnlineWindow;

    /// <summary>
    /// Returns the trimmed IP if <paramref name="value"/> parses as a valid IP
    /// address, otherwise null. Guards against overwriting a good stored LAN IP
    /// with a blank or malformed value from an untrusted agent payload.
    /// </summary>
    private static string? NormalizeLanIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return System.Net.IPAddress.TryParse(trimmed, out _) ? trimmed : null;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Truncate(string? value, int max) =>
        value == null ? null : value.Length <= max ? value : value[..max];
}
