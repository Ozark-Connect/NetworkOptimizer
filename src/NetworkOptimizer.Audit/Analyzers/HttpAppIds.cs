namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Static lookup for HTTP-related application IDs used in UniFi firewall rules.
/// These IDs are hardcoded in UniFi firmware and map to DPI application signatures.
/// </summary>
public static class HttpAppIds
{
    // === Application IDs ===

    /// <summary>
    /// HTTP (port 80) - plain HTTP web traffic
    /// </summary>
    public const int Http = 852190;

    /// <summary>
    /// HTTPS / HTTP over TLS/SSL (port 443) - encrypted web traffic
    /// </summary>
    public const int Https = 1245278;

    /// <summary>
    /// HTTP/3 (QUIC/UDP port 443) - modern web traffic over QUIC
    /// </summary>
    public const int Http3 = 852723;

    /// <summary>
    /// All HTTP-related app IDs for quick membership testing
    /// </summary>
    public static readonly HashSet<int> AllHttpAppIds = new() { Http, Https, Http3 };

    /// <summary>
    /// Check if an app ID is any HTTP-related application
    /// </summary>
    public static bool IsHttpApp(int appId) => AllHttpAppIds.Contains(appId);

    // === Application Category IDs ===

    /// <summary>
    /// Web Services category (includes HTTP, HTTPS, web apps, etc.)
    /// </summary>
    public const int WebServicesCategory = 13;

    /// <summary>
    /// All web-related category IDs
    /// </summary>
    public static readonly HashSet<int> AllWebCategoryIds = new() { WebServicesCategory };

    /// <summary>
    /// Check if an app category ID represents broad web access
    /// </summary>
    public static bool IsWebCategory(int categoryId) => AllWebCategoryIds.Contains(categoryId);
}
