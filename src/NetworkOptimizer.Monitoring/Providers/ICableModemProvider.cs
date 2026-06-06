using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Strategy interface for polling cable modem DOCSIS stats.
/// Implementations encapsulate vendor-specific HTTP scraping (Netgear, ARRIS, etc.)
/// so CableModemMonitorService stays transport-agnostic.
/// </summary>
public interface ICableModemProvider
{
    /// <summary>
    /// Stable identifier used to resolve a provider for a configured cable modem.
    /// Lowercase, hyphenated (e.g. "netgear", "arris-surfboard").
    /// Must be unique across providers.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Human-readable name shown in the UI and logs.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Poll the cable modem and return its current stats.
    /// Implementations should log internally and return null on transport
    /// or parsing failure; throwing is reserved for programming errors.
    /// </summary>
    Task<CableModemStats?> PollAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify connectivity and authentication without performing a full poll.
    /// Used by the Settings page Test button.
    /// </summary>
    Task<(bool Success, string Message)> TestConnectionAsync(
        CmPollContext context,
        CancellationToken cancellationToken = default);
}
