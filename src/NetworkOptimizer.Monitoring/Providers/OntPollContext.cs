namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context for external ONT providers.
/// Decouples IOntProvider from Storage/EF concerns.
/// </summary>
public sealed record OntPollContext
{
    /// <summary>Configuration ID for caching keys and diagnostics.</summary>
    public required int Id { get; init; }

    /// <summary>Friendly name for logs and UI.</summary>
    public required string Name { get; init; }

    /// <summary>Host or IP for the ONT device.</summary>
    public required string Host { get; init; }

    /// <summary>Port; 0 means provider default (typically 80 for HTTP, 22 for SSH).</summary>
    public int Port { get; init; }

    /// <summary>Username for auth (if required by the device).</summary>
    public string? Username { get; init; }

    /// <summary>Password for auth (if required by the device).</summary>
    public string? Password { get; init; }

    /// <summary>Private key path for SSH-based providers.</summary>
    public string? PrivateKeyPath { get; init; }
}
