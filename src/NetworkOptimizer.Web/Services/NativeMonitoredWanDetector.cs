namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Detects WANs whose device UniFi itself monitors through ambient reachability of the
/// device's management IP in the main routing table (verified live: the native Starlink
/// dashboard widget silently breaks when any Monitoring Interface claims the /32 route
/// to the dish's address). Such a WAN's device must always be the ALIASED side of a
/// Monitoring Interface, never the plain one.
///
/// Detection is deliberately a centralized name heuristic - the WAN inventory carries no
/// structured connection-type flag on this path, only the friendly name from the UniFi
/// WAN network config or ISP hostname. Extend the list here when other UniFi-native
/// integrations with the same ambient-reachability dependency appear.
/// </summary>
public static class NativeMonitoredWanDetector
{
    private static readonly string[] NativeMonitoredNameMarkers = { "starlink" };

    public static bool IsUniFiNativeMonitored(string? wanFriendlyName) =>
        !string.IsNullOrWhiteSpace(wanFriendlyName)
        && NativeMonitoredNameMarkers.Any(m =>
            wanFriendlyName.Contains(m, StringComparison.OrdinalIgnoreCase));
}
