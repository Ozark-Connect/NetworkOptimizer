namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context for cable modem providers.
/// Decouples ICableModemProvider from Storage/EF concerns.
/// </summary>
public sealed record CmPollContext
{
    /// <summary>Configuration ID for caching keys and diagnostics.</summary>
    public required int Id { get; init; }

    /// <summary>
    /// Site this configuration belongs to. Providers are singletons shared by
    /// every site, while <see cref="Id"/> only counts within one site's database,
    /// so any provider-side cache must key on <see cref="CacheKey"/>, not Id.
    /// </summary>
    public string SiteSlug { get; init; } = "";

    /// <summary>
    /// Key for provider-side per-device caches (sessions, tokens, discovered
    /// endpoints, last-seen counters). Never key such a cache on Id alone: the
    /// first device added at every site has Id 1.
    /// </summary>
    public string CacheKey => $"{SiteSlug}/{Id}";

    /// <summary>Friendly name for logs and UI.</summary>
    public required string Name { get; init; }

    /// <summary>Host or IP for the cable modem, as configured. <see cref="Dialer"/> decides how it is reached.</summary>
    public required string Host { get; init; }

    /// <summary>
    /// Opens every connection to the device, directly or through the site's agent. HTTP handlers take it as
    /// their ConnectCallback (<see cref="DeviceHttp.Via"/>), so redirects are reached the same way.
    /// </summary>
    public IDeviceDialer Dialer { get; init; } = DirectDeviceDialer.Instance;

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
