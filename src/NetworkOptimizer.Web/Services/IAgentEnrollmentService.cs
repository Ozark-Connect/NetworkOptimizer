using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Issues and manages On-Site Agent enrollments. Gated at the service layer (design doc 06, gate 9)
/// for the admin-facing surface only: the agent tunnel's own calls (enroll with a token, authenticate
/// by key, heartbeat) authenticate with the separate agent scheme (gate 11) and run as system, so they
/// stay on the concrete service and are not user-authorized here.
/// </summary>
[MutatingService]
public interface IAgentEnrollmentService
{
    /// <summary>Agents enrolled against a single site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<SiteAgent>> GetAgentsForSiteAsync(int siteId);

    /// <summary>Every enrolled agent across all sites.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<SiteAgent>> GetAllAgentsAsync();

    /// <summary>Creates an agent record for a site and returns its one-time enrollment token.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AgentEnrolled, Category = AuditCategories.Agent, TargetType = "agent")]
    Task<(SiteAgent Agent, string Token)> CreateAgentAsync(int siteId, string name);

    /// <summary>Issues a fresh enrollment token for an existing agent.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AgentEnrolled, Category = AuditCategories.Agent, TargetType = "agent")]
    Task<string?> ReissueTokenAsync(int agentId);

    /// <summary>Deletes an agent enrollment (the agent can no longer tunnel in).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AgentRemoved, Category = AuditCategories.Agent, TargetType = "agent")]
    Task DeleteAgentAsync(int agentId);

    /// <summary>Enables or disables an agent without deleting its enrollment.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.AgentEnrolled, Category = AuditCategories.Agent, TargetType = "agent")]
    Task SetEnabledAsync(int agentId, bool enabled);

    /// <summary>LAN address reported by the site's online agent, or null when none is connected.</summary>
    [RequireRole(Roles.Viewer)]
    Task<string?> GetOnlineAgentLanIpAsync(string siteSlug);
}
