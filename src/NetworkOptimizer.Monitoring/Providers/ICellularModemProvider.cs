using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Strategy interface for polling cellular modem stats.
/// Implementations encapsulate the transport (SSH+qmicli, HTTP+JSON, future
/// vendors) so CellularModemService can stay transport-agnostic.
/// </summary>
public interface ICellularModemProvider
{
    /// <summary>
    /// Stable identifier used to resolve a provider for a configured modem.
    /// Lowercase, hyphenated, vendor-prefixed (e.g. "qmicli",
    /// "netgear-nighthawk-hotspot"). Must be unique across providers.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Human-readable name shown in the UI and logs.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Poll the modem and return its current stats.
    /// Implementations should log internally and return null on transport
    /// or parsing failure; throwing is reserved for programming errors.
    /// </summary>
    /// <param name="context">Provider-agnostic poll context.</param>
    /// <param name="cancellationToken">Optional cancellation.</param>
    /// <returns>Stats on success, null on failure.</returns>
    Task<CellularModemStats?> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify connectivity and authentication without performing a full poll.
    /// Used by the Settings page Test button.
    /// </summary>
    /// <param name="context">Provider-agnostic poll context.</param>
    /// <param name="cancellationToken">Optional cancellation.</param>
    /// <returns>(success, human-readable message).</returns>
    Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default);
}
