namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// The On-Site Agents that manage a site's devices. Implemented by the web host, which owns the
/// agent records; the collector adds this host itself on the default site.
/// </summary>
public interface IInfrastructureSourceProvider
{
    /// <summary>The site's enrolled, enabled agents with a known LAN IPv4; empty when none.</summary>
    Task<IReadOnlyList<InfrastructureSource>> GetAgentSourcesAsync(string siteSlug, CancellationToken cancellationToken);
}

/// <summary>A host whose device-management traffic on a site is expected.</summary>
/// <param name="Ip">Its LAN IPv4 on the site.</param>
/// <param name="Name">How the filter names it, e.g. "Network Optimizer" or "On-Site Agent Office".</param>
public sealed record InfrastructureSource(string Ip, string Name);
