using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Strategy interface for polling external ONT (Optical Network Terminal) stats.
/// Implementations encapsulate vendor-specific HTTP/SSH scraping (AT&amp;T gateway, etc.)
/// so OntMonitorService stays transport-agnostic.
/// </summary>
public interface IOntProvider
{
    /// <summary>
    /// Stable identifier used to resolve a provider for a configured ONT.
    /// Lowercase, hyphenated (e.g. "att-gateway", "generic-http-ont").
    /// Must be unique across providers.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Human-readable name shown in the UI and logs.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Poll the ONT and return its current stats.
    /// Implementations should log internally and return null on transport
    /// or parsing failure; throwing is reserved for programming errors.
    /// </summary>
    Task<OntStats?> PollAsync(
        OntPollContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify connectivity and authentication without performing a full poll.
    /// Used by the Settings page Test button.
    /// </summary>
    Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context,
        CancellationToken cancellationToken = default);
}
