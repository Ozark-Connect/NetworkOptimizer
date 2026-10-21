using System.Net;

namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// The public addresses a site probes on purpose: its latency targets. Public block lists are cut
/// around them, so a listed transit hop neither reads as total loss nor raises contact alerts.
/// Implemented by the web host, which owns the targets.
/// </summary>
public interface IProbedAddressProvider
{
    /// <summary>The probed addresses of the site in scope, hostnames resolved; empty when none.</summary>
    Task<IReadOnlyList<IPAddress>> GetAsync(CancellationToken cancellationToken);
}
