using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// An ONT provider that can supplement a gateway-slot SFP ONT module with
/// PON-layer statistics. Configurations attached to a monitored SFP module
/// (OntConfiguration.AttachedSfpId) are polled on the gateway SFP collection
/// cycle via <see cref="PollSupplementalAsync"/> and their metrics merged into
/// that module's sfp measurement, instead of being polled standalone.
/// </summary>
public interface ISfpSupplementalOntProvider : IOntProvider
{
    /// <summary>
    /// Poll the endpoint and return PON-layer supplemental stats.
    /// Implementations should log internally and return null on transport
    /// or parsing failure; throwing is reserved for programming errors.
    /// </summary>
    Task<PonSupplementalStats?> PollSupplementalAsync(
        OntPollContext context,
        CancellationToken cancellationToken = default);
}
