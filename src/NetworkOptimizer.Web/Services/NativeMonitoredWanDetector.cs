namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Detects WANs whose device UniFi itself monitors through ambient reachability of the
/// device's management IP in the main routing table (verified live: the native Starlink
/// dashboard widget silently breaks when any Monitoring Interface claims the /32 route
/// to the dish's address). Such a WAN's device must always be the ALIASED side of a
/// Monitoring Interface, never the plain one.
///
/// Detection is a centralized marker heuristic against two signals, whichever is present:
///   1. the WAN's ISP - UniFi's own geo-IP classification (last_geo_info.isp_name), the same
///      value its native UI shows in the ISP column. This is the primary, robust signal: it
///      identifies the connection regardless of what the user named the WAN.
///   2. the WAN's friendly name - a weaker fallback that still catches a WAN the user named
///      "Starlink" when geo-IP hasn't populated yet (e.g. dish just booted behind CGNAT).
/// Extend the marker list when other UniFi-native integrations with the same
/// ambient-reachability dependency appear. ASN is deliberately not used: it adds a second
/// plumbing path for no coverage the marker doesn't already give on this non-blocking advisory.
/// </summary>
public static class NativeMonitoredWanDetector
{
    private static readonly string[] NativeMonitoredMarkers = { "starlink" };

    /// <param name="wanFriendlyName">User-chosen WAN name from the UniFi network config.</param>
    /// <param name="ispName">UniFi geo-IP ISP for the WAN (last_geo_info.isp_name), if known.</param>
    public static bool IsUniFiNativeMonitored(string? wanFriendlyName, string? ispName = null) =>
        MatchesMarker(ispName) || MatchesMarker(wanFriendlyName);

    private static bool MatchesMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && NativeMonitoredMarkers.Any(m =>
            value.Contains(m, StringComparison.OrdinalIgnoreCase));
}
