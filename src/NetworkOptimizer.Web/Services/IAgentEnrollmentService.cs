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
    // An agent belongs to one site, and running that site's agent is part of running the site - so
    // these authorize against the slug the caller names rather than the instance. The three that take
    // an agent id also confirm the agent is on that slug, otherwise naming a site you administer
    // would be enough to act on an agent belonging to one you do not.

    /// <summary>Agents enrolled against a single site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<SiteAgent>> GetAgentsForSiteAsync(int siteId);

    /// <summary>Every enrolled agent across all sites.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<SiteAgent>> GetAllAgentsAsync();

    /// <summary>Creates an agent record for a site and returns its one-time enrollment token.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.AgentEnrolled, Category = AuditCategories.Agent, TargetType = "agent")]
    Task<(SiteAgent Agent, string Token)> CreateAgentAsync([SiteSlug] string siteSlug, string name);

    /// <summary>Issues a fresh enrollment token for an existing agent.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.AgentEnrolled, Category = AuditCategories.Agent, TargetType = "agent")]
    Task<string?> ReissueTokenAsync([SiteSlug] string siteSlug, int agentId);

    /// <summary>Deletes an agent enrollment (the agent can no longer tunnel in).</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.AgentRemoved, Category = AuditCategories.Agent, TargetType = "agent")]
    Task DeleteAgentAsync([SiteSlug] string siteSlug, int agentId);

    /// <summary>Enables or disables an agent without deleting its enrollment.</summary>
    [RequireSiteRole(SiteRole.SiteAdmin)]
    [AuditAction(AuditActions.AgentEnrolled, Category = AuditCategories.Agent, TargetType = "agent")]
    Task SetEnabledAsync([SiteSlug] string siteSlug, int agentId, bool enabled);

    /// <summary>LAN address reported by the site's online agent, or null when none is connected.</summary>
    [RequireRole(Roles.Viewer)]
    Task<string?> GetOnlineAgentLanIpAsync(string siteSlug);
}
