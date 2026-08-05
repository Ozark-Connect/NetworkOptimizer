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
    /// UniFi's interface key for the first WAN group, and the conventional stand-in for "the WAN"
    /// on a site that has only ever had one.
    /// <para>
    /// This is UniFi's key space, not ours - it belongs here with the rest of the console's
    /// conventions. Our own WAN-keyed columns (MonitoringTarget.WanInterface,
    /// WanDiscoveryContext.WanInterface, WanContext.WanInterface) deliberately STORE that key
    /// rather than inventing a parallel one, which is why storage-side fallbacks may reference
    /// this constant. Normalize anything read from storage through
    /// <see cref="WanInterfaceKeyFromKey"/> first: rows written before that normalization
    /// existed can still say "wan1".
    /// </para>
    /// <para>
    /// NOT a synonym for the primary WAN. Group names are arbitrary in UniFi Network and any
    /// group can hold the primary role, so this is only ever a last-resort guess for when the
    /// console cannot say which one does - it is wrong on a site whose primary is WAN2. Ask
    /// UniFiConnectionService.ResolvePrimaryWanNetwork first, and where this value is used as a
    /// fallback, say in a comment that it is a guess and what it costs when it misses.
    /// </para>
    /// </summary>
    public const string DefaultWanKey = "wan";

    /// <summary>
    /// Splits a label produced by <see cref="FormatWanLabel"/> back into the connection's name and
    /// its WAN token ("Acme Fiber WAN2" -> "Acme Fiber", "WAN2"), so a caller can style the two
    /// differently. Name is null when the label carries no name to separate.
    /// <para>
    /// Exact rather than heuristic for the labels this codebase builds for WAN pickers, which pass
    /// no interface or port and therefore have no suffix. A label with a suffix, or one that does
    /// not end in its own WAN token, comes back whole as the name so nothing is silently trimmed.
    /// </para>
    /// </summary>
    public static (string? Name, string? WanToken) SplitWanLabel(string? label, int wanIndex)
    {
        if (string.IsNullOrWhiteSpace(label)) return (null, null);
        var token = wanIndex >= 1 ? $"WAN{wanIndex}" : null;
        if (token == null || !label.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            return (label.Trim(), null);
        var name = label[..^token.Length].Trim();
        return (string.IsNullOrEmpty(name) ? null : name, token);
    }

    /// <summary>
    /// A WAN label for running prose, with the WAN token in parentheses after the connection's
    /// name ("Acme Fiber (WAN2)"). The pill form runs them together because the pill is a label;
    /// a sentence needs the qualifier set apart or it reads as part of the name. Falls back to
    /// whatever there is when a label carries no name or no token.
    /// </summary>
    public static string FormatWanLabelInProse(string? label, int wanIndex)
    {
        var (name, token) = SplitWanLabel(label, wanIndex);
        if (string.IsNullOrEmpty(name)) return token ?? label ?? "";
        return string.IsNullOrEmpty(token) ? name! : $"{name} ({token})";
    }

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
    /// 1-based WAN index from an interface key or wan object key ("wan" and "wan1" → 1,
    /// "wan2" → 2). Zero for anything that is not a wan key, which
    /// <see cref="FormatWanLabel"/> reads as "no WAN label".
    /// </summary>
    /// <summary>
    /// Whether a WAN's uplink interface is a UniFi cellular modem. The gateway reaches an attached
    /// 5G/LTE modem over a GRE tunnel, and nothing else on a UniFi gateway presents as <c>gre*</c>,
    /// so the interface name identifies the medium outright rather than by inference.
    /// </summary>
    public static bool IsCellularUplink(string? uplinkIfName) =>
        uplinkIfName?.TrimStart().StartsWith("gre", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A WAN token cut down to its index for tight layouts ("WAN2" -> "2"), where the column is
    /// narrow enough that repeating "WAN" on every row costs more than it says. Anything that is
    /// not a token - a connection name, from a label that carried none - comes back untouched.
    /// </summary>
    public static string ShortWanToken(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label ?? "";
        var index = WanIndexFromKey(label);
        return index >= 1 ? index.ToString(System.Globalization.CultureInfo.InvariantCulture) : label;
    }

    public static int WanIndexFromKey(string? wanKey)
    {
        if (string.IsNullOrWhiteSpace(wanKey)) return 0;
        var trimmed = wanKey.Trim();
        if (string.Equals(trimmed, "wan", StringComparison.OrdinalIgnoreCase)) return 1;
        return trimmed.StartsWith("wan", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[3..], out var index) && index >= 1
            ? index
            : 0;
    }

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
