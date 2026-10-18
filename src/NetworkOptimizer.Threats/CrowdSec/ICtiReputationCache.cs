using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.CrowdSec;

/// <summary>
/// CrowdSec CTI answers, one per IP for the whole instance: every site (and, in a multi-tenant host,
/// every tenant) reads the same row, so an address is looked up once whichever key paid for it.
/// Never add a site or tenant column: that brings back one lookup per site for the same IP.
/// Non-public addresses are never read or stored. The table is left out of config exports.
/// </summary>
public interface ICtiReputationCache
{
    /// <summary>The unexpired answer for <paramref name="ip"/>, or null.</summary>
    Task<CrowdSecReputation?> GetAsync(string ip, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces the answer for the reputation's IP.</summary>
    Task SaveAsync(CrowdSecReputation reputation, CancellationToken cancellationToken = default);

    /// <summary>Deletes expired answers.</summary>
    Task PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
