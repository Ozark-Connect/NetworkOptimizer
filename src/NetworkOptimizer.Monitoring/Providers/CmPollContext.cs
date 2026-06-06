namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context for cable modem providers.
/// Decouples ICableModemProvider from Storage/EF concerns.
/// </summary>
public sealed record CmPollContext
{
    /// <summary>Configuration ID for caching keys and diagnostics.</summary>
    public required int Id { get; init; }

    /// <summary>Friendly name for logs and UI.</summary>
    public required string Name { get; init; }

    /// <summary>Host or IP for the cable modem.</summary>
    public required string Host { get; init; }

    /// <summary>HTTP port; 0 means provider default (typically 80).</summary>
    public int Port { get; init; }

    /// <summary>Username for HTTP auth (default "admin" for most modems).</summary>
    public string? Username { get; init; }

    /// <summary>Password for HTTP auth.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Override for the status page URL path.
    /// If null/empty, provider uses its built-in default
    /// (e.g. "/DocsisStatus.asp" for Netgear, "/cmconnectionstatus.html" for ARRIS).
    /// </summary>
    public string? StatusPagePath { get; init; }
}
