using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

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

    /// <summary>
    /// The policy that decides new traffic from a network to an address and port, or null (see <see cref="Allows"/>).
    /// Matching and order are the Audit's (<see cref="FirewallRuleParser"/>, <see cref="FirewallRule"/>,
    /// <see cref="FirewallRuleEvaluator"/>): this only finds the zone pair and maps the deciding rule back to its policy.
    /// </summary>
    public static JsonObject? Decides(ConfigState state, string sourceNetworkId, string address, string protocol, int port)
    {
        state = WithCustomZoneDefaults(state);
        var zoneOf = ZoneOfNetwork(state);
        if (!zoneOf.TryGetValue(sourceNetworkId, out var src)) return null;
        var (dst, dstNetwork) = ZoneOfAddress(state, zoneOf, address);
        if (dst == null) return null;
        var source = SourceNetwork(state, sourceNetworkId, src);
        if (source == null) return null;

        // Only the zone pair's policies are parsed: the rest can never decide this traffic.
        var pair = state.Get("firewall_policy").Where(p => string.Equals(SrcZone(p), src, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Str(p["destination"]?["zone_id"]), dst, StringComparison.OrdinalIgnoreCase)).ToList();
        var rules = Parse(state, pair);
        var decided = FirewallRuleEvaluator.Evaluate(rules.Keys,
            // A rule scoped to some hosts or source ports of the network does not decide for the network as a whole.
            // An unresolved port group leaves no ports, which the helper reads as all ports: it matches nothing instead.
            r => r.AppliesToSourceNetwork(source, IpFamily.IPv4) && string.IsNullOrEmpty(r.SourcePort)
                 && r.AppliesToDestination(dstNetwork, address) && !r.HasUnresolvedDestinationPortGroup
                 && FirewallGroupHelper.RuleMatchesPortAndProtocol(r, port.ToString(System.Globalization.CultureInfo.InvariantCulture), protocol),
            forNewConnections: true, family: IpFamily.IPv4).EffectiveRule;
        return decided != null ? rules[decided] : null;
    }

    /// <summary>Whether a policy's source covers a network as a whole, by the Audit's source matching.</summary>
    public static bool SourceCovers(ConfigState state, JsonObject policy, string network) =>
        SourceNetwork(state, network, ZoneOfNetwork(state).GetValueOrDefault(network)) is { } source
        && Parse(state, [policy]).Keys.SingleOrDefault() is { } rule
        && rule.AppliesToSourceNetwork(source, IpFamily.IPv4) && string.IsNullOrEmpty(rule.SourcePort);

    /// <summary>
    /// Policies parsed by the Audit's <see cref="FirewallRuleParser"/>, with the state's groups for flattening, each
    /// mapped back to its policy. A policy without an index is one a plan creates; UniFi puts it ahead of the
    /// predefined ones, so it is parsed at the first custom index.
    /// </summary>
    public static Dictionary<FirewallRule, JsonObject> Parse(ConfigState state, IEnumerable<JsonObject> policies)
    {
        var parser = new FirewallRuleParser(NullLogger<FirewallRuleParser>.Instance);
        parser.SetFirewallGroups(FirewallGroups(state).Values);
        var rules = new Dictionary<FirewallRule, JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var policy in policies)
        {
            var copy = (JsonObject)policy.DeepClone();
            if (copy["index"] is not JsonValue) copy["index"] = FirstCustomIndex;
            using var doc = JsonDocument.Parse(copy.ToJsonString());
            if (parser.ParseFirewallPolicy(doc.RootElement) is { } rule) rules[rule] = policy;
        }
        return rules;
    }

    private const int FirstCustomIndex = 10000;

    /// <summary>The state's firewall groups by ID, typed as the Audit reads them. A console can list one twice; the first stands.</summary>
    public static Dictionary<string, UniFiFirewallGroup> FirewallGroups(ConfigState state)
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>();
        foreach (var g in state.Get("firewallgroup"))
        {
            try
            {
                if (JsonSerializer.Deserialize<UniFiFirewallGroup>(g.ToJsonString()) is { Id.Length: > 0 } group) groups.TryAdd(group.Id, group);
            }
            catch (JsonException) { }
        }
        return groups;
    }

    /// <summary>A network as the Audit's source matching reads it: its ID, zone, VLAN, and IPv4 subnet.</summary>
    private static NetworkInfo? SourceNetwork(ConfigState state, string networkId, string? zone)
    {
        var net = state.Get("networkconf").FirstOrDefault(n => string.Equals(ConfigState.IdOf(n), networkId, StringComparison.OrdinalIgnoreCase));
        if (net == null && zone == null) return null;
        return new NetworkInfo
        {
            Id = networkId, Name = net != null ? Str(net["name"]) ?? networkId : networkId,
            VlanId = net?["vlan"] is JsonValue v && v.TryGetValue<int>(out var vlan) ? vlan : 1,
            Subnet = net != null ? Str(net["ip_subnet"]) : null, FirewallZoneId = zone,
        };
    }

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

    /// <summary>
    /// Whether a policy already does what a candidate would: every field the candidate writes holds the same value, its name,
    /// ID, order, and description aside. This is config identity, not traffic, so it compares the stored JSON: the parsed
    /// model drops fields and would call two different stored policies the same. Matched by content because a zone rename
    /// changes the names plans give their policies and a Network upgrade can re-key every ID (10.6 to 11).
    /// </summary>
    public static bool Holds(JsonObject existing, JsonObject candidate)
    {
        if (Bool(existing["predefined"]) == true) return false;
        foreach (var (key, want) in candidate)
            if (key is not ("_id" or "id" or "name" or "index" or "description") && !Covers(existing[key], want)) return false;
        return true;

        static bool Covers(JsonNode? have, JsonNode? want) => want is JsonObject w
            ? have is JsonObject h && w.All(p => Covers(h[p.Key], p.Value))
            : CanonicalJson.DeepEquals(have, want);
    }

    /// <summary>A zone without a zone key: one the operator or a plan created.</summary>
    public static bool IsCustom(JsonObject zone) => string.IsNullOrEmpty(Str(zone["zone_key"]));

    private static bool IsPredefined(JsonObject p) => Bool(p["predefined"]) == true;

    private static string? SrcZone(JsonObject p) => Str(p["source"]?["zone_id"]);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
