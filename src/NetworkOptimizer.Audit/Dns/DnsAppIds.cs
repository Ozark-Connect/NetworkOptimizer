namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Static lookup for DNS-related application IDs used in UniFi firewall rules.
/// These IDs are hardcoded in UniFi firmware and map to DPI application signatures.
/// App IDs are port-based under the hood.
/// </summary>
public static class DnsAppIds
{
    /// <summary>
    /// DNS (UDP port 53) - traditional DNS queries
    /// </summary>
    public const int Dns = 589885;

    /// <summary>
    /// DNS over TLS (port 853) - covers DoT (TCP) and DoQ (UDP) when protocol includes both
    /// </summary>
    public const int DnsOverTls = 1310917;

    /// <summary>
    /// DNS over HTTPS (port 443) - covers DoH (TCP) and DoH3 (UDP/QUIC) when protocol includes both
    /// </summary>
    public const int DnsOverHttps = 1310919;

    /// <summary>
    /// All DNS-related app IDs for quick membership testing
    /// </summary>
    public static readonly HashSet<int> AllDnsAppIds = new() { Dns, DnsOverTls, DnsOverHttps };

    /// <summary>
    /// Check if an app ID is any DNS-related application
    /// </summary>
    public static bool IsDnsApp(int appId) => AllDnsAppIds.Contains(appId);

    /// <summary>
    /// Check if an app ID is DNS (port 53) - traditional DNS
    /// </summary>
    public static bool IsDns53App(int appId) => appId == Dns;

    /// <summary>
    /// Check if an app ID is port 853 (DoT/DoQ)
    /// </summary>
    public static bool IsPort853App(int appId) => appId == DnsOverTls;

    /// <summary>
    /// Check if an app ID is port 443 (DoH/DoH3)
    /// </summary>
    public static bool IsPort443App(int appId) => appId == DnsOverHttps;
}
