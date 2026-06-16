using System.Text.Json;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Shared primitives for interpreting a UniFi gateway's WAN configuration so that
/// every monitoring consumer derives WAN network groups and interface keys the same
/// way. Selection of WHICH WAN is primary/active lives elsewhere
/// (UniFiConnectionService.ResolvePrimaryWanNetwork for the configured primary,
/// UniFiDiscovery.ResolveActiveWanInterface for the live uplink); these helpers only
/// translate a known WAN into its conventional names.
/// </summary>
public static class GatewayWanHelper
{
    /// <summary>
    /// UniFi network-group convention for a 1-based WAN index: wan1 → "WAN",
    /// wanN → "WANn".
    /// </summary>
    public static string WanNetworkGroup(int wanIndex) => wanIndex == 1 ? "WAN" : $"WAN{wanIndex}";

    /// <summary>
    /// Lowercase interface-key convention for a 1-based WAN index: wan1 → "wan",
    /// wanN → "wann". Matches port_table.network_name for the primary WAN.
    /// </summary>
    public static string WanInterfaceKey(int wanIndex) => wanIndex == 1 ? "wan" : $"wan{wanIndex}";

    /// <summary>
    /// Network-group convention from a wan object key ("wan"/"wan1" → "WAN",
    /// "wan2" → "WAN2"). Used when iterating GetWanInterfaces() whose Key is the
    /// raw JSON property name.
    /// </summary>
    public static string WanNetworkGroupFromKey(string wanKey)
        => string.Equals(wanKey, "wan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(wanKey, "wan1", StringComparison.OrdinalIgnoreCase)
            ? "WAN"
            : wanKey.ToUpperInvariant();

    /// <summary>
    /// Builds an ifname → networkgroup map from a gateway's <c>ethernet_overrides</c>
    /// JSON array (e.g. "eth6" → "WAN"). Returns an empty case-insensitive map when the
    /// element is absent or not an array.
    /// </summary>
    public static Dictionary<string, string> BuildNetworkGroupByIfname(JsonElement ethernetOverrides)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ethernetOverrides.ValueKind != JsonValueKind.Array)
            return map;

        foreach (var ov in ethernetOverrides.EnumerateArray())
        {
            var ifn = ov.TryGetProperty("ifname", out var ifnP) ? ifnP.GetString() : null;
            var ng = ov.TryGetProperty("networkgroup", out var ngP) ? ngP.GetString() : null;
            if (!string.IsNullOrEmpty(ifn) && !string.IsNullOrEmpty(ng))
                map[ifn] = ng;
        }
        return map;
    }
}
