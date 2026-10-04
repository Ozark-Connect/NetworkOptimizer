namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context for Starlink terminal providers.
/// Decouples IStarlinkProvider from Storage/EF concerns.
/// </summary>
public sealed record StarlinkPollContext
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

    /// <summary>Host or IP of the dish's gRPC endpoint, as configured. <see cref="Dialer"/> decides how it is reached.</summary>
    public required string Host { get; init; }

    /// <summary>Opens every connection to the dish, directly or through the site's agent.</summary>
    public IDeviceDialer Dialer { get; init; } = DirectDeviceDialer.Instance;

    /// <summary>gRPC port; 0 means provider default (9200).</summary>
    public int Port { get; init; }
}
