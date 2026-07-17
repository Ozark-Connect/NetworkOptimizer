namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Detects WANs that are Starlink connections so the UI can steer users toward the native
/// Starlink monitoring (dish gRPC polling with full history) instead of pointing generic
/// tools like Monitoring Interfaces at the dish.
///
/// Detection is a marker match against two signals, whichever is present:
///   1. the WAN's ISP - UniFi's own geo-IP classification (last_geo_info.isp_name), the same
///      value its native UI shows in the ISP column and the same signal UniFi's own console
///      builds use to find Starlink WANs. This is the primary, robust signal: it identifies
///      the connection regardless of what the user named the WAN.
///   2. the WAN's friendly name - a weaker fallback that still catches a WAN the user named
///      "Starlink" when geo-IP hasn't populated yet (e.g. dish just booted behind CGNAT).
/// ASN is deliberately not used: it adds a second plumbing path for no coverage the marker
/// doesn't already give on a non-blocking advisory.
/// </summary>
public static class StarlinkWanDetector
{
    /// <param name="wanFriendlyName">User-chosen WAN name from the UniFi network config.</param>
    /// <param name="ispName">UniFi geo-IP ISP for the WAN (last_geo_info.isp_name), if known.</param>
    public static bool IsStarlinkWan(string? wanFriendlyName, string? ispName = null) =>
        MatchesMarker(ispName) || MatchesMarker(wanFriendlyName);

    private static bool MatchesMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains("starlink", StringComparison.OrdinalIgnoreCase);
}
