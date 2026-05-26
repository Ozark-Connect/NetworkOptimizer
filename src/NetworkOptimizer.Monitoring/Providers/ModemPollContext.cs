namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context.
/// Decouples ICellularModemProvider from Storage/EF concerns - callers
/// construct this from their own configuration source (ModemConfiguration,
/// in-memory test fixtures, future settings backends, etc.).
/// </summary>
public sealed record ModemPollContext
{
    /// <summary>Identifier for diagnostics and caching keys.</summary>
    public required int Id { get; init; }

    /// <summary>Friendly name for logs and UI.</summary>
    public required string Name { get; init; }

    /// <summary>Host or IP for the modem.</summary>
    public required string Host { get; init; }

    /// <summary>Port; 0 means provider default.</summary>
    public int Port { get; init; }

    /// <summary>Optional username; provider decides whether to use it.</summary>
    public string? Username { get; init; }

    /// <summary>Optional password; provider decides whether to use it.</summary>
    public string? Password { get; init; }

    /// <summary>Optional private key path; provider decides whether to use it.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// Modem model or type string for display and provider-internal branching
    /// (e.g. "U5G-Max", "U-LTE"). Not a primary routing key - that is ProviderKey.
    /// </summary>
    public string ModemType { get; init; } = string.Empty;

    /// <summary>
    /// Transport device path or similar provider-specific locator.
    /// For QMI providers this is the QMI device path (e.g. /dev/wwan0qmi0).
    /// Unused by transports that do not need it.
    /// </summary>
    public string TransportPath { get; init; } = string.Empty;
}
