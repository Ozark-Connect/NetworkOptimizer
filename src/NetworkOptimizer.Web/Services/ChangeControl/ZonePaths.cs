using System.Text.Json.Nodes;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// The zone-based firewall as UniFi evaluates it, over a <see cref="ConfigState"/>: the predefined
/// policies UniFi generates for a custom zone, and whether traffic from a network reaches an
/// address on a port. Used to check a planned config before it is written.
/// </summary>
[VendorSpecific("UniFi", "predefined zone-pair policies for a custom zone; v2 firewall policy matching")]
public static class ZonePaths
{
    private const long PredefinedIndex = 2147483647;

    /// <summary>
    /// The state with UniFi's predefined policies added for each custom zone that has none yet (a zone the
    /// plan creates). UniFi writes them when the zone is created (UDR7 on Network 11, 2026-10-30):
    /// Block All to and from every other LAN, VPN, Hotspot, and DMZ zone and within the zone itself; Allow
    /// All to External and to and from the Gateway. Never written: they are predefined, so no plan writes them.
    /// </summary>
    public static ConfigState WithCustomZoneDefaults(ConfigState state)
    {
        if (!state.Collections.ContainsKey("firewall_zone") || !state.Collections.ContainsKey("firewall_policy")) return state;
        var zones = state.Get("firewall_zone");
        var policies = state.Get("firewall_policy");
        var predefinedSources = policies.Where(IsPredefined).Select(p => SrcZone(p)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = zones.Where(z => IsCustom(z) && !predefinedSources.Contains(ConfigState.IdOf(z))).ToList();
        if (fresh.Count == 0) return state;

        var copy = state.Clone();
        var list = copy.Collections["firewall_policy"];
        string? Keyed(string key) => zones.FirstOrDefault(z => Str(z["zone_key"]) == key) is { } z ? ConfigState.IdOf(z) : null;
        var (external, gateway) = (Keyed("external"), Keyed("gateway"));
        var lan = zones.Where(z => IsCustom(z) || Str(z["zone_key"]) is "internal" or "vpn" or "hotspot" or "dmz").Select(ConfigState.IdOf).ToList();
        var n = 0;
        void Add(string? src, string? dst, string action, string name, string states = "ALL")
        {
            if (src == null || dst == null) return;
            list.Add(new JsonObject
            {
                ["_id"] = $"predefined-{++n}-{src}-{dst}", ["name"] = name, ["action"] = action, ["enabled"] = true, ["predefined"] = true,
                ["index"] = PredefinedIndex, ["protocol"] = "all", ["ip_version"] = "BOTH", ["connection_state_type"] = states,
                ["source"] = new JsonObject { ["zone_id"] = src, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY" },
                ["destination"] = new JsonObject { ["zone_id"] = dst, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY" },
            });
        }
        foreach (var zone in fresh.Select(ConfigState.IdOf))
        {
            foreach (var other in lan.Where(o => !string.Equals(o, zone, StringComparison.OrdinalIgnoreCase)))
            {
                Add(zone, other, "BLOCK", "Block All Traffic");
                // Another new custom zone gets this pair from its own loop.
                if (!fresh.Any(f => string.Equals(ConfigState.IdOf(f), other, StringComparison.OrdinalIgnoreCase)))
                    Add(other, zone, "BLOCK", "Block All Traffic");
            }
            Add(zone, zone, "BLOCK", "Block All Traffic");
            Add(zone, external, "ALLOW", "Allow All Traffic");
            Add(external, zone, "ALLOW", "Allow Return Traffic", "RESPOND_ONLY");
            Add(external, zone, "BLOCK", "Block All Traffic");
            Add(zone, gateway, "ALLOW", "Allow All Traffic");
            Add(gateway, zone, "ALLOW", "Allow All Traffic");
        }
        return copy;
    }

    /// <summary>
    /// Whether new traffic from <paramref name="sourceNetworkId"/> reaches <paramref name="address"/> on a
    /// protocol and port: the first matching policy for the zone pair decides. Null when it cannot be told
    /// (the network is in no zone, or no policy matches). A policy scoped to some hosts of the network
    /// (IPs, clients) does not decide for the network as a whole; one excluding a few hosts does.
    /// </summary>
    public static bool? Allows(ConfigState state, string sourceNetworkId, string address, string protocol, int port) =>
        Decides(state, sourceNetworkId, address, protocol, port) is { } policy ? Str(policy["action"]) == "ALLOW" : null;

    /// <summary>The policy that decides new traffic from a network to an address and port, or null (see <see cref="Allows"/>).</summary>
    public static JsonObject? Decides(ConfigState state, string sourceNetworkId, string address, string protocol, int port)
    {
        state = WithCustomZoneDefaults(state);
        var zoneOf = ZoneOfNetwork(state);
        if (!zoneOf.TryGetValue(sourceNetworkId, out var src)) return null;
        var (dst, dstNetwork) = ZoneOfAddress(state, zoneOf, address);
        if (dst == null) return null;

        var groups = state.Get("firewallgroup").GroupBy(ConfigState.IdOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var candidates = state.Get("firewall_policy")
            .Where(p => Bool(p["enabled"]) != false && Str(p["ip_version"]) != "IPV6"
                        && string.Equals(SrcZone(p), src, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Str(p["destination"]?["zone_id"]), dst, StringComparison.OrdinalIgnoreCase))
            // A policy without an index is one a plan creates; UniFi puts it ahead of the predefined ones.
            .OrderBy(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : 10000L);
        foreach (var p in candidates)
        {
            if (!SourceMatches(p["source"] as JsonObject, sourceNetworkId)) continue;
            if (!DestinationMatches(p["destination"] as JsonObject, dstNetwork, address, port, groups)) continue;
            if (!ProtocolMatches(Str(p["protocol"]), protocol)) continue;
            var states = Str(p["connection_state_type"]);
            var allow = Str(p["action"]) == "ALLOW";
            if (allow && states == "RESPOND_ONLY") continue;
            // A CUSTOM state set without NEW (Block Invalid Traffic) never decides a new connection.
            if (states == "CUSTOM" && !(p["connection_states"] as JsonArray ?? []).Any(s => Str(s) == "NEW")) continue;
            return p;
        }
        return null;
    }

    /// <summary>A policy side covers the network: ANY, its network listed, or everyone but a few hosts.</summary>
    public static bool SourceCovers(JsonObject? side, string network) => SourceMatches(side, network);

    /// <summary>Network ID to the ID of the zone that lists it.</summary>
    public static Dictionary<string, string> ZoneOfNetwork(ConfigState state)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in state.Get("firewall_zone"))
            foreach (var id in (zone["network_ids"] as JsonArray ?? []).Select(Str).OfType<string>())
                map[id] = ConfigState.IdOf(zone);
        return map;
    }

    /// <summary>
    /// The zone an address is in, and its network: a network's own gateway address is the Gateway zone,
    /// an address inside a LAN subnet is that network's zone, anything else is External.
    /// </summary>
    public static (string? Zone, string? Network) ZoneOfAddress(ConfigState state, Dictionary<string, string> zoneOf, string address)
    {
        var zones = state.Get("firewall_zone");
        string? Keyed(string key) => zones.FirstOrDefault(z => Str(z["zone_key"]) == key) is { } z ? ConfigState.IdOf(z) : null;
        if (Readdressing.ParseIp(address) is not { } ip) return (null, null);
        foreach (var n in state.Get("networkconf"))
        {
            var cidr = Str(n["ip_subnet"]);
            if (Readdressing.ParseSubnet(cidr) is not { } subnet || !subnet.Contains(ip)) continue;
            if (Readdressing.ParseIp(cidr!.Split('/')[0]) == ip) return (Keyed("gateway"), null);
            return (zoneOf.GetValueOrDefault(ConfigState.IdOf(n)), ConfigState.IdOf(n));
        }
        return (Keyed("external"), null);
    }

    private static bool SourceMatches(JsonObject? match, string network)
    {
        if (match == null) return false;
        return Str(match["matching_target"]) switch
        {
            null or "ANY" => true,
            "NETWORK" => (match["network_ids"] as JsonArray ?? []).Any(n => string.Equals(Str(n), network, StringComparison.OrdinalIgnoreCase))
                         != (Bool(match["match_opposite_networks"]) == true),
            // Everyone but a few hosts covers the network; a few hosts do not.
            "IP" => Bool(match["match_opposite_ips"]) == true,
            _ => false,
        };
    }

    private static bool DestinationMatches(JsonObject? match, string? network, string address, int port, Dictionary<string, JsonObject> groups)
    {
        if (match == null) return false;
        var target = Str(match["matching_target"]) switch
        {
            null or "ANY" => true,
            "NETWORK" => network != null && (match["network_ids"] as JsonArray ?? []).Any(n => string.Equals(Str(n), network, StringComparison.OrdinalIgnoreCase))
                         != (Bool(match["match_opposite_networks"]) == true),
            "IP" => (match["ips"] as JsonArray ?? []).Select(Str).OfType<string>().Any(e => InRange(e, address)) != (Bool(match["match_opposite_ips"]) == true),
            _ => false,
        };
        if (!target) return false;
        var ports = Str(match["port_matching_type"]) switch
        {
            "SPECIFIC" => Str(match["port"]),
            "OBJECT" => Str(match["port_group_id"]) is { } gid && groups.TryGetValue(gid, out var g)
                ? string.Join(",", (g["group_members"] as JsonArray ?? []).Select(Str)) : null,
            _ => "",
        };
        if (ports == null) return false;
        if (ports.Length == 0) return true;
        return PortListContains(ports, port) != (Bool(match["match_opposite_ports"]) == true);
    }

    private static bool ProtocolMatches(string? policy, string protocol) => policy switch
    {
        null or "all" => true,
        "tcp_udp" => protocol is "tcp" or "udp",
        _ => policy == protocol,
    };

    /// <summary>A comma list of ports and ranges ("53,853,8000-8010") includes <paramref name="port"/>.</summary>
    public static bool PortListContains(string list, int port) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part =>
        {
            var range = part.Split('-');
            return range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi)
                ? port >= lo && port <= hi
                : int.TryParse(part, out var single) && single == port;
        });

    /// <summary>An address entry (single, range "a-b", or CIDR) includes <paramref name="address"/>.</summary>
    public static bool InRange(string entry, string address)
    {
        if (Readdressing.ParseIp(address) is not { } ip) return false;
        if (entry.Contains('/')) return Readdressing.ParseSubnet(entry) is { } s && s.Contains(ip);
        var range = entry.Split('-');
        if (range.Length == 2)
            return Readdressing.ParseIp(range[0].Trim()) is { } lo && Readdressing.ParseIp(range[1].Trim()) is { } hi && ip >= lo && ip <= hi;
        return Readdressing.ParseIp(entry.Trim()) == ip;
    }

    /// <summary>A zone without a zone key: one the operator or a plan created.</summary>
    public static bool IsCustom(JsonObject zone) => string.IsNullOrEmpty(Str(zone["zone_key"]));

    private static bool IsPredefined(JsonObject p) => Bool(p["predefined"]) == true;

    private static string? SrcZone(JsonObject p) => Str(p["source"]?["zone_id"]);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
