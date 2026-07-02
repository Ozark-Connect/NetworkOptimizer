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
    /// Exchanges a one-time enrollment token for a long-lived agent key.
    /// Returns the raw key and the site slug the agent should operate under.
    /// </summary>
    public async Task<(bool Success, string? AgentKey, string? SiteSlug, string? Error)> EnrollAsync(string token, string? version)
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

        var site = await db.Sites.FindAsync(agent.SiteId);
        if (site == null)
            return (false, null, null, "Agent's site no longer exists");

        var key = KeyPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        agent.AgentKeyHash = Hash(key);
        agent.EnrolledAt = DateTime.UtcNow;
        agent.LastSeenAt = DateTime.UtcNow;
        agent.LastVersion = Truncate(version, 32);
        agent.EnrollmentTokenHash = null;
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _logger.LogInformation("Agent {Name} (id {Id}) enrolled for site {Slug}", agent.Name, agent.Id, site.Slug);
        return (true, key, site.Slug, null);
    }

    /// <summary>Records a heartbeat for an enrolled agent, keyed by its agent key.</summary>
    public async Task<bool> HeartbeatAsync(string agentKey, string? version)
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
        agent.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Truncate(string? value, int max) =>
        value == null ? null : value.Length <= max ? value : value[..max];
}
