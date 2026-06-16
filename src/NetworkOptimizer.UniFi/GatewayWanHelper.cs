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
    /// Builds a human-readable WAN label from up to four identifiers
    /// (e.g. "Acme Fiber WAN1 (eth6 - Port 7)"), degrading gracefully when any
    /// piece is missing so it never emits empty parentheses, doubled spaces, or
    /// "null". The connection name and WAN index form the prefix; the physical
    /// interface and port label form a parenthesized suffix. When neither name nor a
    /// valid WAN index is present, falls back to the interface name, then to
    /// "Unknown WAN".
    /// </summary>
    /// <param name="connectionName">ISP/connection name (GatewayWanInterface.Name), if any</param>
    /// <param name="wanIndex">1-based WAN index (1 → "WAN1"); &lt;= 0 omits the WAN label</param>
    /// <param name="ifName">Physical interface name (e.g. "eth6"), if any</param>
    /// <param name="portLabel">Front-panel port label (e.g. "Port 7"), if any</param>
    public static string FormatWanLabel(string? connectionName, int wanIndex, string? ifName, string? portLabel)
    {
        var name = string.IsNullOrWhiteSpace(connectionName) ? null : connectionName.Trim();
        var iface = string.IsNullOrWhiteSpace(ifName) ? null : ifName.Trim();
        var port = string.IsNullOrWhiteSpace(portLabel) ? null : portLabel.Trim();
        var wanLabel = wanIndex >= 1 ? $"WAN{wanIndex}" : null;

        // Drop a port label that just repeats another part (common when the port is named
        // after the ISP), so we don't render "Acme Fiber WAN4 (eth1 - Acme Fiber)".
        if (port != null && (
                string.Equals(port, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(port, iface, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(port, wanLabel, StringComparison.OrdinalIgnoreCase)))
        {
            port = null;
        }

        var prefix = string.Join(" ", new[] { name, wanLabel }.Where(p => !string.IsNullOrEmpty(p)));
        var suffixParts = new List<string?> { iface, port };

        if (string.IsNullOrEmpty(prefix))
        {
            // No name and no WAN index: fall back to the interface as the prefix so it
            // isn't repeated in the suffix; last resort is a generic label.
            if (iface != null)
            {
                prefix = iface;
                suffixParts = new List<string?> { port };
            }
            else
            {
                prefix = "Unknown WAN";
            }
        }

        var suffix = string.Join(" - ", suffixParts.Where(p => !string.IsNullOrEmpty(p)));
        return string.IsNullOrEmpty(suffix) ? prefix : $"{prefix} ({suffix})";
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
