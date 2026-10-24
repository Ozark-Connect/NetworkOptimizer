using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>What happened to one legacy rule.</summary>
public enum LegacyRuleStatus { Translated, Covered, Unproven }

/// <summary>One legacy rule and the zone policies it became.</summary>
public sealed class LegacyRuleOutcome
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Ruleset { get; init; } = "";
    public bool Enabled { get; init; }
    public LegacyRuleStatus Status { get; init; }
    public string Detail { get; init; } = "";
    public int Policies { get; init; }
}

/// <summary>A translated config and the per-rule outcomes.</summary>
public sealed class LegacyTranslation
{
    public ConfigState Desired { get; init; } = new();
    public List<LegacyRuleOutcome> Rules { get; init; } = [];
    public IEnumerable<LegacyRuleOutcome> Unproven => Rules.Where(r => r.Status == LegacyRuleStatus.Unproven);
}

/// <summary>
/// Translates legacy firewall rules (<c>rest/firewallrule</c>, legacy-full-firewallrules.json) into
/// zone-based policies on a zone-based console (spec "Legacy firewall to ZBF migration"). Rules map
/// per ruleset to zone pairs; address matches land in every zone whose networks they overlap, so the
/// IP match itself keeps the scope. A rule that cannot be mapped exactly is Unproven and stops at
/// Review Plan; the Security Audit then compares the legacy config with the translated one.
/// </summary>
[VendorSpecific("UniFi", "legacy firewallrule fields and rulesets; zone policy body from sample-firewall-policies-*.txt")]
public static class LegacyFirewallTranslator
{
    private const string PolicyPrefix = "legacy-policy-";

    // Policy protocols seen in the zone-based captures; anything else has no proven equivalent.
    private static readonly HashSet<string> Protocols = new(StringComparer.OrdinalIgnoreCase) { "all", "tcp", "udp", "tcp_udp", "icmp", "icmpv6" };

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;
    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static List<string> Strs(JsonObject o, string key) =>
        (o[key] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>().Where(s => s.Length > 0).ToList();

    /// <summary>Whether the source has legacy rules and the target runs zones.</summary>
    public static bool Applies(ConfigState source, ConfigState live) =>
        source.Get("firewall_zone").Count == 0 && source.Get("firewallrule").Count > 0 && live.Get("firewall_zone").Count > 0;

    /// <param name="source">The legacy config (a snapshot).</param>
    /// <param name="live">The zone-based console.</param>
    /// <param name="deleteMissing">The plan deletes live networks the source lacks, so zones drop them too.</param>
    public static LegacyTranslation Translate(ConfigState source, ConfigState live, bool deleteMissing)
    {
        var desired = source.Clone();
        desired.Collections.Remove("firewallrule");
        var networks = source.Get("networkconf");
        var groups = source.Get("firewallgroup").ToDictionary(ConfigState.IdOf);

        var zones = MapZones(networks, live, deleteMissing);
        desired.Collections["firewall_zone"] = zones.Objects;

        var lan = networks.Where(n => Str(n, "purpose") is "corporate" or "guest").ToList();
        var guests = lan.Where(n => Str(n, "purpose") == "guest").Select(ConfigState.IdOf).ToHashSet();
        var subnets = lan.Select(n => (Id: ConfigState.IdOf(n), Range: Cidr(Str(n, "ip_subnet")))).Where(s => s.Range != null)
            .Select(s => (s.Id, Range: s.Range!.Value)).ToList();

        var policies = new List<JsonObject>();
        var outcomes = new List<LegacyRuleOutcome>();
        var n = 0;

        foreach (var rule in source.Get("firewallrule").OrderBy(r => Ruleset(r)).ThenBy(r => r["rule_index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue))
        {
            var ctx = new RuleContext(rule, groups, zones, lan, guests, subnets);
            var (status, detail, built) = TranslateRule(ctx);
            foreach (var p in built)
            {
                p["_id"] = PolicyPrefix + ++n;
                p["index"] = n;
                policies.Add(p);
            }
            outcomes.Add(new LegacyRuleOutcome
            {
                Id = ConfigState.IdOf(rule), Name = Str(rule, "name") ?? "", Ruleset = Str(rule, "ruleset") ?? "",
                Enabled = rule["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on,
                Status = status, Detail = detail, Policies = built.Count,
            });
        }

        // UniFi's own predefined and derived policies stay; custom ones come from the translation.
        var generated = ConfigCollections.Get("firewall_policy")!.IsGenerated;
        desired.Collections["firewall_policy"] = live.Get("firewall_policy").Where(p => generated?.Invoke(p) == true)
            .Select(p => (JsonObject)p.DeepClone()).Concat(policies).ToList();
        return new LegacyTranslation { Desired = desired, Rules = outcomes };
    }

    private static string Ruleset(JsonObject rule) => (Str(rule, "ruleset") ?? "").ToUpperInvariant();

    private sealed record ZoneMap(List<JsonObject> Objects, Dictionary<string, string> ZoneOfNetwork, Dictionary<string, string> ByKey, List<string> LanZones);

    /// <summary>
    /// The console's zones with the source's networks in them: a network the console already has
    /// keeps its zone, a new one goes to Internal (Hotspot for a guest network).
    /// </summary>
    private static ZoneMap MapZones(IReadOnlyList<JsonObject> networks, ConfigState live, bool deleteMissing)
    {
        var objects = live.Get("firewall_zone").Select(z => (JsonObject)z.DeepClone()).ToList();
        var byKey = objects.Where(z => Str(z, "zone_key") is { Length: > 0 }).GroupBy(z => Str(z, "zone_key")!).ToDictionary(g => g.Key, g => ConfigState.IdOf(g.First()));
        var liveZoneOf = new Dictionary<string, JsonObject>();
        foreach (var z in objects)
            foreach (var id in Strs(z, "network_ids")) liveZoneOf[id] = z;

        // The plan's own network key, so a network keeps the zone of the live network the plan matches it to.
        string Key(JsonObject net) => ConfigCollections.NetworkKey(net) ?? "id:" + ConfigState.IdOf(net);
        var liveByKey = live.Get("networkconf").GroupBy(Key).ToDictionary(g => g.Key, g => g.First());

        var zoneOf = new Dictionary<string, string>();
        foreach (var z in objects) z["network_ids"] = new JsonArray();
        foreach (var net in networks.Where(x => Str(x, "purpose") is "corporate" or "guest"))
        {
            var id = ConfigState.IdOf(net);
            var zone = liveByKey.TryGetValue(Key(net), out var match) && liveZoneOf.TryGetValue(ConfigState.IdOf(match), out var current)
                ? current
                : objects.FirstOrDefault(z => ConfigState.IdOf(z) == byKey.GetValueOrDefault(Str(net, "purpose") == "guest" && byKey.ContainsKey("hotspot") ? "hotspot" : "internal"));
            if (zone == null) continue;
            ((JsonArray)zone["network_ids"]!).Add(id);
            zoneOf[id] = ConfigState.IdOf(zone);
        }
        // Networks the source does not have keep their zones: WAN and VPN always, a LAN unless the plan deletes it.
        var sourceIds = networks.Select(ConfigState.IdOf).ToHashSet();
        var matchedLive = networks.Select(x => liveByKey.GetValueOrDefault(Key(x))).OfType<JsonObject>().Select(ConfigState.IdOf).ToHashSet();
        var liveNets = live.Get("networkconf").ToDictionary(ConfigState.IdOf);
        foreach (var (liveId, zone) in liveZoneOf)
        {
            if (matchedLive.Contains(liveId) || sourceIds.Contains(liveId)) continue;
            if (deleteMissing && liveNets.TryGetValue(liveId, out var net) && Str(net, "purpose") is "corporate" or "guest") continue;
            ((JsonArray)zone["network_ids"]!).Add(liveId);
        }

        var lanZones = objects.Where(z => Str(z, "zone_key") is not ("external" or "gateway" or "vpn")).Select(ConfigState.IdOf).ToList();
        return new ZoneMap(objects, zoneOf, byKey, lanZones);
    }

    private sealed record RuleContext(JsonObject Rule, Dictionary<string, JsonObject> Groups, ZoneMap Zones, List<JsonObject> Lan,
        HashSet<string> Guests, List<(string Id, IpRange Range)> Subnets);

    /// <summary>One side of a policy: where it matches and in which zones.</summary>
    private sealed record Side(JsonObject Match, List<string> Zones);

    private static (LegacyRuleStatus, string, List<JsonObject>) TranslateRule(RuleContext c)
    {
        var r = c.Rule;
        var ruleset = Ruleset(r);
        var v6 = ruleset.Contains("V6_");
        var baseSet = ruleset.Replace("V6_", "_");
        bool est = Bool(r, "state_established"), rel = Bool(r, "state_related"), inv = Bool(r, "state_invalid"), nw = Bool(r, "state_new");
        var action = Str(r, "action")?.ToLowerInvariant();

        if (est || rel || inv || nw)
        {
            // The zone-based firewall answers established/related traffic and drops invalid itself.
            if (est && rel && !inv && !nw && action == "accept") return (LegacyRuleStatus.Covered, "The zone-based firewall allows return traffic itself.", []);
            if (inv && !est && !rel && !nw && action is "drop" or "reject") return (LegacyRuleStatus.Covered, "The zone-based firewall drops invalid traffic itself.", []);
            return Unproven("Matches on connection state; no proven zone policy equivalent.");
        }
        if (baseSet.EndsWith("_OUT") && baseSet != "WAN_OUT")
            return Unproven($"{Str(r, "ruleset")} filters traffic leaving toward a network; zones have no equivalent.");
        if (!string.IsNullOrEmpty(Str(r, "ipsec"))) return Unproven("Matches IPsec traffic.");
        if (!string.IsNullOrEmpty(Str(r, "icmp_typename"))) return Unproven("Matches an ICMP type.");
        var protocol = Str(r, "protocol") is { Length: > 0 } p ? p : "all";
        if (!Protocols.Contains(protocol)) return Unproven($"Protocol {protocol} has no proven zone policy equivalent.");
        if (action is not ("accept" or "drop" or "reject")) return Unproven($"Action {action} is not known.");

        var gateway = c.Zones.ByKey.GetValueOrDefault("gateway");
        var external = c.Zones.ByKey.GetValueOrDefault("external");
        var vpn = c.Zones.ByKey.GetValueOrDefault("vpn");

        // Source and destination zones per ruleset (legacy chain semantics, FirewallRuleParser.MapRulesetToZones).
        Side? src, dst;
        string? why;
        switch (baseSet)
        {
            case "LAN_IN" or "GUEST_IN" or "LAN_LOCAL" or "GUEST_LOCAL":
                var guest = baseSet.StartsWith("GUEST");
                (src, why) = Source(c, guest, external, vpn);
                if (src == null) return Unproven(why!);
                (dst, why) = baseSet.EndsWith("_LOCAL")
                    ? LocalDestination(c, gateway)
                    : Destination(c, c.Zones.LanZones.Concat(new[] { external, vpn }.OfType<string>()).ToList(), external, vpn);
                break;
            case "WAN_IN":
                if (external == null) return Unproven("The console has no External zone.");
                if (Str(r, "src_networkconf_id") is { Length: > 0 }) return Unproven("Matches a source network on WAN_IN.");
                (src, why) = Addresses(c, "src", external, [], null, null);
                if (src == null) return Unproven(why!);
                (dst, why) = Destination(c, c.Zones.LanZones, null, null);
                break;
            case "WAN_LOCAL":
                if (external == null || gateway == null) return Unproven("The console has no External or Gateway zone.");
                if (Str(r, "src_networkconf_id") is { Length: > 0 }) return Unproven("Matches a source network on WAN_LOCAL.");
                (src, why) = Addresses(c, "src", external, [], null, null);
                if (src == null) return Unproven(why!);
                (dst, why) = LocalDestination(c, gateway);
                break;
            case "WAN_OUT":
                if (external == null) return Unproven("The console has no External zone.");
                if (Str(r, "dst_networkconf_id") is { Length: > 0 }) return Unproven("Matches a destination network on WAN_OUT.");
                (src, why) = Source(c, null, null, null);
                if (src == null) return Unproven(why!);
                (dst, why) = Addresses(c, "dst", external, [], null, null);
                break;
            default:
                return Unproven($"Ruleset {Str(r, "ruleset")} is not known.");
        }
        if (dst == null) return Unproven(why!);

        var (srcPort, srcPortWhy) = Ports(c, "src");
        var (dstPort, dstPortWhy) = Ports(c, "dst");
        if (srcPortWhy != null) return Unproven(srcPortWhy);
        if (dstPortWhy != null) return Unproven(dstPortWhy);

        var built = new List<JsonObject>();
        foreach (var s in src.Zones.Distinct())
            foreach (var d in dst.Zones.Distinct())
            {
                var source = (JsonObject)src.Match.DeepClone();
                source["zone_id"] = s;
                foreach (var kv in srcPort) source[kv.Key] = kv.Value?.DeepClone();
                var destination = (JsonObject)dst.Match.DeepClone();
                destination["zone_id"] = d;
                foreach (var kv in dstPort) destination[kv.Key] = kv.Value?.DeepClone();
                var allow = action == "accept";
                built.Add(new JsonObject
                {
                    ["name"] = Str(r, "name") ?? "", ["description"] = $"Translated from legacy {Str(r, "ruleset")}",
                    ["enabled"] = r["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var on) || on,
                    ["action"] = allow ? "ALLOW" : "BLOCK", ["ip_version"] = v6 ? "IPV6" : "IPV4", ["protocol"] = protocol,
                    ["connection_state_type"] = "ALL", ["connection_states"] = new JsonArray(), ["create_allow_respond"] = allow,
                    ["logging"] = Bool(r, "logging"), ["match_ip_sec"] = false, ["match_opposite_protocol"] = Bool(r, "protocol_match_excepted"),
                    ["icmp_typename"] = "ANY", ["icmp_v6_typename"] = "ANY", ["predefined"] = false, ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
                    ["source"] = source, ["destination"] = destination,
                });
            }
        var note = action == "reject" ? "Reject becomes Block. " : "";
        return (LegacyRuleStatus.Translated, $"{note}{built.Count} {(built.Count == 1 ? "policy" : "policies")}.", built);

        static (LegacyRuleStatus, string, List<JsonObject>) Unproven(string why) => (LegacyRuleStatus.Unproven, why, []);
    }

    /// <summary>A LAN-side source: a network, addresses, a MAC, or any network the ruleset covers.</summary>
    private static (Side?, string?) Source(RuleContext c, bool? guest, string? external, string? vpn)
    {
        var r = c.Rule;
        if (Str(r, "src_mac_address") is { Length: > 0 }) return (null, "Matches a client MAC; its zone cannot be proven.");
        if (Str(r, "src_networkconf_id") is { Length: > 0 } netId)
        {
            if (Str(r, "src_networkconf_type") is { } t && !t.StartsWith("NET", StringComparison.OrdinalIgnoreCase))
                return (null, "Matches a network's gateway address, not its subnet.");
            if (!c.Zones.ZoneOfNetwork.TryGetValue(netId, out var zone)) return (null, "Its source network has no zone on the console.");
            return (new Side(new JsonObject { ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray(netId), ["match_opposite_networks"] = false, ["match_mac"] = false }, [zone]), null);
        }
        var covered = c.Lan.Where(n => guest == null || c.Guests.Contains(ConfigState.IdOf(n)) == guest).Select(ConfigState.IdOf).ToList();
        // Legacy guest networks have their own rulesets, so a LAN rule matching any source is only exact without them.
        if (guest == false && c.Guests.Count > 0 && !HasAddresses(r, "src"))
            return (null, "Matches any source on a site with guest networks; which rulesets cover them is not proven.");
        var anyZones = covered.Select(id => c.Zones.ZoneOfNetwork.GetValueOrDefault(id)).OfType<string>().Distinct().ToList();
        return Addresses(c, "src", null, anyZones, external, vpn, lanOnly: true, ambiguous: guest == false ? c.Guests : null);
    }

    private static bool HasAddresses(JsonObject r, string side) =>
        Str(r, $"{side}_address") is { Length: > 0 } || Strs(r, $"{side}_firewallgroup_ids").Count > 0;

    /// <summary>A forwarded destination: a network, addresses, or anything (every zone given).</summary>
    private static (Side?, string?) Destination(RuleContext c, List<string> anyZones, string? external, string? vpn)
    {
        var r = c.Rule;
        if (Str(r, "dst_networkconf_id") is { Length: > 0 } netId)
        {
            if (Str(r, "dst_networkconf_type") is { } t && !t.StartsWith("NET", StringComparison.OrdinalIgnoreCase))
                return (null, "Matches a network's gateway address, not its subnet.");
            if (!c.Zones.ZoneOfNetwork.TryGetValue(netId, out var zone)) return (null, "Its destination network has no zone on the console.");
            return (new Side(new JsonObject { ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray(netId), ["match_opposite_networks"] = false }, [zone]), null);
        }
        return Addresses(c, "dst", null, anyZones, external, vpn);
    }

    /// <summary>Traffic to the gateway itself: any address on it, or the addresses given.</summary>
    private static (Side?, string?) LocalDestination(RuleContext c, string? gateway)
    {
        if (gateway == null) return (null, "The console has no Gateway zone.");
        var r = c.Rule;
        if (Str(r, "dst_networkconf_id") is { Length: > 0 }) return (null, "Matches the gateway on one network; no proven zone policy equivalent.");
        return Addresses(c, "dst", gateway, [], null, null);
    }

    /// <summary>
    /// An address side: one address group stays a group reference, several are flattened. With a
    /// fixed zone (External, Gateway) the addresses stay in it. Otherwise the zones are every LAN zone
    /// the addresses overlap, plus External and VPN for any part outside the LANs (not for a LAN
    /// source, which only ever comes from a LAN), limited to the zones the ruleset covers.
    /// </summary>
    private static (Side?, string?) Addresses(RuleContext c, string side, string? fixedZone, List<string> anyZones, string? external, string? vpn,
        bool lanOnly = false, HashSet<string>? ambiguous = null)
    {
        var r = c.Rule;
        var single = Str(r, $"{side}_address");
        var groupIds = Strs(r, $"{side}_firewallgroup_ids");
        var addressGroups = new List<JsonObject>();
        foreach (var id in groupIds)
        {
            if (!c.Groups.TryGetValue(id, out var g)) return (null, "It names a firewall group the snapshot does not have.");
            if (Str(g, "group_type") is "address-group" or "ipv6-address-group") addressGroups.Add(g);
            else if (Str(g, "group_type") != "port-group") return (null, $"Firewall group {Str(g, "name")} is a {Str(g, "group_type")}.");
        }
        var members = addressGroups.SelectMany(g => Strs(g, "group_members")).ToList();
        if (single is { Length: > 0 }) members.Add(single);
        if (members.Count == 0 && addressGroups.Count == 0)
            return (new Side(new JsonObject { ["matching_target"] = "ANY" }, fixedZone != null ? [fixedZone] : anyZones), null);
        if (members.Count == 0) return (null, "Its address group is empty.");

        var zones = new HashSet<string>();
        var outside = false;
        foreach (var m in members)
        {
            if (Range(m) is not { } range) return (null, $"Address {m} is not an IP, subnet, or range.");
            var hits = c.Subnets.Where(s => s.Range.Overlaps(range)).ToList();
            if (ambiguous != null && hits.Any(h => ambiguous.Contains(h.Id)))
                return (null, "Matches addresses on a guest network under a LAN ruleset; which rulesets cover guest networks is not proven.");
            foreach (var h in hits)
                if (c.Zones.ZoneOfNetwork.TryGetValue(h.Id, out var z)) zones.Add(z);
            if (!hits.Any(h => range.Within(h.Range))) outside = true;
        }
        if (fixedZone != null) zones = [fixedZone];
        else
        {
            if (outside && !lanOnly)
            {
                if (external != null) zones.Add(external);
                if (vpn != null) zones.Add(vpn);
            }
            zones.IntersectWith(anyZones);
        }
        if (zones.Count == 0) return (null, "Its addresses are on no network the console has.");

        JsonObject match = addressGroups.Count == 1 && single is not { Length: > 0 }
            ? new JsonObject { ["matching_target"] = "IP", ["matching_target_type"] = "OBJECT", ["ip_group_id"] = ConfigState.IdOf(addressGroups[0]), ["ips"] = new JsonArray(), ["match_opposite_ips"] = false }
            : new JsonObject { ["matching_target"] = "IP", ["matching_target_type"] = "SPECIFIC", ["ips"] = new JsonArray(members.Distinct().Select(m => (JsonNode?)m).ToArray()), ["match_opposite_ips"] = false };
        if (side == "src") match["match_mac"] = false;
        return (new Side(match, zones.ToList()), null);
    }

    /// <summary>The port fields of one side: a port string, one port group, or several flattened.</summary>
    private static (Dictionary<string, JsonNode?>, string?) Ports(RuleContext c, string side)
    {
        var r = c.Rule;
        var port = Str(r, $"{side}_port");
        var portGroups = Strs(r, $"{side}_firewallgroup_ids").Select(id => c.Groups.GetValueOrDefault(id)).OfType<JsonObject>()
            .Where(g => Str(g, "group_type") == "port-group").ToList();
        var result = new Dictionary<string, JsonNode?> { ["match_opposite_ports"] = false };
        if (portGroups.Count == 1 && port is not { Length: > 0 })
        {
            result["port_matching_type"] = "OBJECT";
            result["port_group_id"] = ConfigState.IdOf(portGroups[0]);
        }
        else if (portGroups.Count > 0 || port is { Length: > 0 })
        {
            var ports = portGroups.SelectMany(g => Strs(g, "group_members")).ToList();
            if (port is { Length: > 0 }) ports.Add(port);
            if (ports.Count == 0) return (result, "Its port group is empty.");
            result["port_matching_type"] = "SPECIFIC";
            result["port"] = string.Join(",", ports.Distinct());
        }
        else result["port_matching_type"] = "ANY";
        return (result, null);
    }

    /// <summary>An address range of one family.</summary>
    internal readonly record struct IpRange(bool V6, UInt128 Lo, UInt128 Hi)
    {
        public bool Overlaps(IpRange o) => V6 == o.V6 && Lo <= o.Hi && o.Lo <= Hi;
        public bool Within(IpRange o) => V6 == o.V6 && o.Lo <= Lo && Hi <= o.Hi;
    }

    internal static IpRange? Cidr(string? cidr)
    {
        if (cidr is not { Length: > 0 } || cidr.Split('/') is not [var ip, var bits] || !IPAddress.TryParse(ip, out var addr) || !int.TryParse(bits, out var len)) return null;
        var v6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
        var width = v6 ? 128 : 32;
        if (len < 0 || len > width) return null;
        var host = width - len == 128 ? UInt128.MaxValue : (UInt128.One << (width - len)) - 1;
        var lo = ToNumber(addr) & ~host;
        return new IpRange(v6, lo, lo | host);
    }

    /// <summary>An IP, CIDR, or a-b range.</summary>
    internal static IpRange? Range(string member)
    {
        member = member.Trim();
        if (member.Contains('/')) return Cidr(member);
        if (member.Split('-') is [var a, var b] && IPAddress.TryParse(a.Trim(), out var lo) && IPAddress.TryParse(b.Trim(), out var hi) && lo.AddressFamily == hi.AddressFamily)
            return new IpRange(lo.AddressFamily == AddressFamily.InterNetworkV6, ToNumber(lo), ToNumber(hi));
        return IPAddress.TryParse(member, out var one) ? new IpRange(one.AddressFamily == AddressFamily.InterNetworkV6, ToNumber(one), ToNumber(one)) : null;
    }

    private static UInt128 ToNumber(IPAddress addr)
    {
        UInt128 v = 0;
        foreach (var b in addr.GetAddressBytes()) v = (v << 8) | b;
        return v;
    }
}
