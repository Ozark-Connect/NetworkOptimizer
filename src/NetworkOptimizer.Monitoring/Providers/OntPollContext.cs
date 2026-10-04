namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Provider-agnostic poll context for external ONT providers.
/// Decouples IOntProvider from Storage/EF concerns.
/// </summary>
public sealed record OntPollContext
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

    /// <summary>Host or IP for the ONT device, as configured. <see cref="Dialer"/> decides how it is reached.</summary>
    public required string Host { get; init; }

    /// <summary>
    /// Opens every connection to the device, directly or through the site's agent. HTTP handlers take it as
    /// their ConnectCallback (<see cref="DeviceHttp.Via"/>), so redirects are reached the same way.
    /// </summary>
    public IDeviceDialer Dialer { get; init; } = DirectDeviceDialer.Instance;

    /// <summary>Port; 0 means provider default (typically 80 for HTTP, 22 for SSH).</summary>
    public int Port { get; init; }

    /// <summary>Username for auth (if required by the device).</summary>
    public string? Username { get; init; }

    /// <summary>Password for auth (if required by the device).</summary>
    public string? Password { get; init; }

    /// <summary>Private key path for SSH-based providers.</summary>
    /// <summary>
    /// Set from the ONT's configuration and read by nothing: no ONT provider uses SSH yet. Its field
    /// in Settings - Monitoring is commented out under "ONT SSH KEY ANCHOR" until one does.
    /// </summary>
    public string? PrivateKeyPath { get; init; }
}
