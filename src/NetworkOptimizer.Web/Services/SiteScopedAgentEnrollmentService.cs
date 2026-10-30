using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The caller-facing <see cref="IAgentEnrollmentService"/>, built per scope by the gated registration
/// so the agent lists narrow to the caller's sites. <see cref="AgentEnrollmentService"/> is a singleton
/// and cannot hold the scoped site filter: captured at construction, it resolves from the root scope,
/// where there is no caller, and narrows nothing.
/// </summary>
public sealed class SiteScopedAgentEnrollmentService(
    AgentEnrollmentService inner,
    Authorization.ISiteAccessFilter siteAccess) : IAgentEnrollmentService
{
    /// <inheritdoc />
    public Task<List<SiteAgent>> GetAgentsForSiteAsync(int siteId) => inner.GetAgentsForSiteAsync(siteId, siteAccess);

    /// <inheritdoc />
    public Task<List<SiteAgent>> GetAllAgentsAsync() => inner.GetAllAgentsAsync(siteAccess);

    /// <inheritdoc />
    public Task<(SiteAgent Agent, string Token)> CreateAgentAsync(string siteSlug, string name) =>
        inner.CreateAgentAsync(siteSlug, name);

    /// <inheritdoc />
    public Task<string?> ReissueTokenAsync(string siteSlug, int agentId) => inner.ReissueTokenAsync(siteSlug, agentId);

    /// <inheritdoc />
    public Task DeleteAgentAsync(string siteSlug, int agentId) => inner.DeleteAgentAsync(siteSlug, agentId);

    /// <inheritdoc />
    public Task SetEnabledAsync(string siteSlug, int agentId, bool enabled) =>
        inner.SetEnabledAsync(siteSlug, agentId, enabled);

    /// <inheritdoc />
    public Task<string?> GetOnlineAgentLanIpAsync(string siteSlug) => inner.GetOnlineAgentLanIpAsync(siteSlug);
}
