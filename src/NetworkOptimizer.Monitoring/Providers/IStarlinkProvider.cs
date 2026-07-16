using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Strategy interface for polling Starlink user terminal stats.
/// Implementations encapsulate the transport (the dish's local gRPC API)
/// so StarlinkMonitorService stays transport-agnostic.
/// </summary>
public interface IStarlinkProvider
{
    /// <summary>
    /// Stable identifier used to resolve a provider for a configured terminal.
    /// Lowercase, hyphenated (e.g. "starlink-grpc").
    /// Must be unique across providers.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Human-readable name shown in the UI and logs.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Poll the terminal and return its current stats.
    /// Implementations should log internally and return null on transport
    /// or parsing failure; throwing is reserved for programming errors.
    /// </summary>
    Task<StarlinkStats?> PollAsync(
        StarlinkPollContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch the dish's obstruction sky map. Larger payload than a status
    /// poll, so callers fetch it on a slower cadence than PollAsync.
    /// Returns null on transport failure.
    /// </summary>
    Task<StarlinkObstructionMap?> GetObstructionMapAsync(
        StarlinkPollContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify connectivity without performing a full poll.
    /// Used by the Settings page Test button.
    /// </summary>
    Task<(bool Success, string Message)> TestConnectionAsync(
        StarlinkPollContext context,
        CancellationToken cancellationToken = default);
}
