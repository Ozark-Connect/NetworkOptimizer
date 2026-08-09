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
    /// Implementations should log internally and report transport or parsing failure through
    /// <see cref="ModemPollResult.Failed"/>; throwing is reserved for programming errors.
    /// Every failure carries a reason: it is what the Settings test and the Dashboard card show.
    /// </summary>
    /// <param name="context">Provider-agnostic poll context.</param>
    /// <param name="cancellationToken">Optional cancellation.</param>
    /// <returns>Stats on success, or the reason the poll produced none.</returns>
    Task<ModemPollResult> PollAsync(
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

/// <summary>
/// Optional capability for providers that can power-cycle the modem's radio.
/// Kept separate from <see cref="ICellularModemProvider"/> so vendors with no
/// equivalent control are not forced to stub it out.
/// </summary>
public interface ISupportsRadioReset
{
    /// <summary>
    /// Take the radio to low power and back online, forcing a fresh cell selection.
    /// Drops the cellular connection for the duration, so callers must confirm with
    /// the user before invoking it.
    /// </summary>
    /// <param name="context">Provider-agnostic poll context.</param>
    /// <param name="cancellationToken">Optional cancellation.</param>
    /// <returns>(success, human-readable message).</returns>
    Task<(bool success, string message)> ResetRadioAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default);
}
