using System.Text.Json;
using NetworkOptimizer.Core;

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
    /// Lowercase interface-key from a wan object key ("wan"/"wan1" → "wan",
    /// "wan2" → "wan2"). The wanN counterpart of <see cref="WanInterfaceKey"/> for
    /// callers iterating <see cref="EnumerateWanInterfaces"/>.
    /// </summary>
    public static string WanInterfaceKeyFromKey(string wanKey)
        => string.Equals(wanKey, "wan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(wanKey, "wan1", StringComparison.OrdinalIgnoreCase)
            ? "wan"
            : wanKey.ToLowerInvariant();

    /// <summary>
    /// Enumerates a gateway's wan1..wan6 objects from raw device JSON as typed
    /// <see cref="Models.GatewayWanInterface"/> values (Key set to the source property),
    /// reusing the same per-object deserialization as
    /// <see cref="Models.UniFiDeviceResponse.GetWanInterfaces"/>. Covers the wan1..wan6 keys
    /// that gateways actually report (not the keyless "wan" that GetWanInterfaces' regex also
    /// accepts) - matching the wan{i} loops this replaces. Lets the monitoring parsers read
    /// WAN fields (ifname, uplink_ifname, ip, port_idx, speed) without each hand-rolling the
    /// loop. Objects that fail to deserialize are skipped.
    /// </summary>
    [VendorSpecific("UniFi", "Parses UniFi gateway wan1..wan6 device JSON into typed GatewayWanInterface")]
    public static IEnumerable<Models.GatewayWanInterface> EnumerateWanInterfaces(JsonElement device)
    {
        for (var i = 1; i <= 6; i++)
        {
            var key = $"wan{i}";
            if (!device.TryGetProperty(key, out var wanObj) || wanObj.ValueKind != JsonValueKind.Object)
                continue;

            Models.GatewayWanInterface? wan = null;
            try
            {
                wan = wanObj.Deserialize<Models.GatewayWanInterface>();
            }
            catch (JsonException)
            {
                // Mirror GetWanInterfaces(): skip a malformed wan object rather than throw.
            }

            if (wan == null)
                continue;

            wan.Key = key;
            yield return wan;
        }
    }

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
    [VendorSpecific("UniFi", "Parses UniFi gateway ethernet_overrides JSON array (ifname -> networkgroup)")]
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
