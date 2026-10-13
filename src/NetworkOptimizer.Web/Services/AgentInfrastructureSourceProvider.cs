using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// A site's enrolled, enabled On-Site Agents by LAN IPv4, for the collector's Infrastructure filters.
/// Offline agents count: an agent's address is still its own while its tunnel is down.
/// </summary>
public sealed class AgentInfrastructureSourceProvider(IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory)
    : IInfrastructureSourceProvider
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<InfrastructureSource>> GetAgentSourcesAsync(string siteSlug, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        await using var db = await mainDbFactory.CreateDbContextAsync(cancellationToken);
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == slug, cancellationToken);
        if (site == null)
            return [];

        var agents = await db.SiteAgents.AsNoTracking()
            .Where(a => a.SiteId == site.Id && a.Enabled && a.EnrolledAt != null && a.LanIp != null)
            .Select(a => new { a.Name, a.LanIp })
            .ToListAsync(cancellationToken);

        return agents
            .Where(a => IPAddress.TryParse(a.LanIp, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new InfrastructureSource(a.LanIp!,
                string.IsNullOrWhiteSpace(a.Name) ? "On-Site Agent" : $"On-Site Agent {a.Name}"))
            .ToList();
    }
}
