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
public class AgentEnrollmentService
{
    /// <summary>Agents reporting within this window count as online.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    /// <summary>Unused enrollment tokens stop working after this long.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private const string TokenPrefix = "noa_";
    private const string KeyPrefix = "noak_";

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<AgentEnrollmentService> _logger;

    public AgentEnrollmentService(
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<AgentEnrollmentService> logger)
    {
        _mainDbFactory = mainDbFactory;
        _logger = logger;
    }

    /// <summary>Agents registered for a site, newest first.</summary>
    public async Task<List<SiteAgent>> GetAgentsForSiteAsync(int siteId)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        return await db.SiteAgents
            .Where(a => a.SiteId == siteId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    /// <summary>All agents across sites (for the overview card).</summary>
    public async Task<List<SiteAgent>> GetAllAgentsAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        return await db.SiteAgents.ToListAsync();
    }

    /// <summary>
    /// Registers a new agent for a site and returns its one-time enrollment
    /// token. The token is not retrievable afterwards.
    /// </summary>
    public async Task<(SiteAgent Agent, string Token)> CreateAgentAsync(int siteId, string name)
    {
        var token = TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var agent = new SiteAgent
        {
            SiteId = siteId,
            Name = string.IsNullOrWhiteSpace(name) ? "Agent" : name.Trim(),
            EnrollmentTokenHash = Hash(token),
            TokenCreatedAt = DateTime.UtcNow,
        };

        await using var db = await _mainDbFactory.CreateDbContextAsync();
        db.SiteAgents.Add(agent);
        await db.SaveChangesAsync();
        _logger.LogInformation("Created agent {Name} (id {Id}) for site {SiteId}", agent.Name, agent.Id, siteId);
        return (agent, token);
    }

    /// <summary>
    /// Issues a fresh one-time enrollment token for an existing agent that has
    /// not enrolled yet, so re-entering setup for a site reuses the same agent
    /// row instead of piling up duplicates. Returns null if the agent is gone or
    /// already enrolled.
    /// </summary>
    public async Task<string?> ReissueTokenAsync(int agentId)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await db.SiteAgents.FindAsync(agentId);
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
    public async Task DeleteAgentAsync(int agentId)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await db.SiteAgents.FindAsync(agentId);
        if (agent == null)
            return;

        db.SiteAgents.Remove(agent);
        await db.SaveChangesAsync();
        _logger.LogInformation("Removed agent {Name} (id {Id}) for site {SiteId}", agent.Name, agent.Id, agent.SiteId);
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
    /// or null when the site has no such agent (default site, no agent, agent
    /// offline, or its LAN IP is not yet known). Used to point site clients at the
    /// on-site agent for LAN speed tests.
    /// </summary>
    public async Task<string?> GetOnlineAgentLanIpAsync(string siteSlug)
    {
        if (string.IsNullOrWhiteSpace(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug)
            return null;

        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == siteSlug);
        if (site == null)
            return null;

        var agent = await db.SiteAgents.AsNoTracking()
            .Where(a => a.SiteId == site.Id && a.Enabled && a.EnrolledAt != null && a.LanIp != null)
            .OrderByDescending(a => a.LastSeenAt)
            .FirstOrDefaultAsync();

        return agent != null && IsOnline(agent.LastSeenAt) ? agent.LanIp : null;
    }

    /// <summary>Enables or disables an agent. Disabled agents cannot enroll or heartbeat.</summary>
    public async Task SetEnabledAsync(int agentId, bool enabled)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var agent = await db.SiteAgents.FindAsync(agentId);
        if (agent == null)
            return;

        agent.Enabled = enabled;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
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
