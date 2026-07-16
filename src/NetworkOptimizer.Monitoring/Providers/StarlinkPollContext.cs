namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context for Starlink terminal providers.
/// Decouples IStarlinkProvider from Storage/EF concerns.
/// </summary>
public sealed record StarlinkPollContext
{
    /// <summary>Configuration ID for caching keys and diagnostics.</summary>
    public required int Id { get; init; }

    /// <summary>Friendly name for logs and UI.</summary>
    public required string Name { get; init; }

    /// <summary>Host or IP of the dish's gRPC endpoint. On agent-routed sites this is the tunnel-proxy loopback endpoint, not the dish's own address.</summary>
    public required string Host { get; init; }

    /// <summary>The configured device host before any tunnel-proxy rewrite; use for logs so failures name the real device.</summary>
    public string? ConfiguredHost { get; init; }

    /// <summary>gRPC port; 0 means provider default (9200).</summary>
    public int Port { get; init; }
}
