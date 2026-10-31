using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using IssueTypes = NetworkOptimizer.Audit.IssueTypes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A Security Audit DNS finding's fix: the desired config and what Review Plan should say about it.</summary>
public sealed record DnsFix(ConfigState Desired, List<string> Notes, string Title);

/// <summary>
/// Fix it for me for DNS findings: extends the operator's own rule (a firewall
/// block or a DNAT redirect) to the networks it does not cover, copying it once per zone, and builds UniFi's
/// usual shape only when the site has no rule to copy. Coverage is read from the config as the firewall evaluates
/// it, and the planned audit then confirms the finding clears.
/// </summary>
[VendorSpecific("UniFi", "v2 firewall policy and DNAT shapes; FIREWALL_GROUPS, NETWORK_CONF, ADDRESS_AND_PORT filters")]
public static class DnsFixes
{
    /// <summary>The findings v1 fixes.</summary>
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        IssueTypes.DnsDnatPartialCoverage, IssueTypes.DnsNo53Block, IssueTypes.Dns53PartialCoverage,
        IssueTypes.DnsNoDotBlock, IssueTypes.DnsNoDoqBlock, IssueTypes.DnsNoDohBlock,
    };

    // TEST-NET-3: any address outside the site's subnets is the External zone.
    private const string Internet = "203.0.113.53";

    private enum Kind { Dnat, Port53, Dot, Doq, Doh }

    private static Kind? KindOf(string type) => type switch
    {
        IssueTypes.DnsDnatPartialCoverage => Kind.Dnat,
        IssueTypes.DnsNo53Block or IssueTypes.Dns53PartialCoverage => Kind.Port53,
        IssueTypes.DnsNoDotBlock => Kind.Dot,
        IssueTypes.DnsNoDoqBlock => Kind.Doq,
        IssueTypes.DnsNoDohBlock => Kind.Doh,
        _ => null,
    };

    private static string TitleOf(Kind kind) => kind switch
    {
        Kind.Dnat => "DNS: DNAT Partial Coverage",
        Kind.Port53 => "DNS: Port 53 Not Blocked",
        Kind.Dot => "DNS: DoT Not Blocked",
        Kind.Doq => "DNS: DoQ Not Blocked",
        _ => "DNS: DoH Not Blocked",
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static bool Off(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && !b;

    /// <summary>
    /// The fix for a finding, or null when there is nothing this can fix: every network in scope is covered, or
    /// the fix needs a choice only the operator can make (a DNAT target on a site with no DNAT to copy).
    /// </summary>
    /// <param name="live">Live config.</param>
    /// <param name="issueType">The finding's issue type.</param>
    /// <param name="dohDomains">DoH provider hostnames for a new DoH block; the analyzer's registry by default.</param>
    /// <param name="allowGuestPublicDns">Leave Guest and Hotspot networks alone (Settings: Allow Guest / Hotspot Public DNS).</param>
    public static DnsFix? Build(ConfigState live, string issueType, IReadOnlyList<string>? dohDomains = null, bool allowGuestPublicDns = false)
    {
        if (KindOf(issueType) is not { } kind || live.Get("firewall_zone").Count == 0) return null;
        var desired = live.Clone();
        desired.Collections["firewall_policy"] = desired.Get("firewall_policy").Select(p => (JsonObject)p.DeepClone()).ToList();
        desired.Collections["nat"] = desired.Get("nat").Select(p => (JsonObject)p.DeepClone()).ToList();
        var notes = new List<string>();
        // Coverage is read without UniFi's Hotspot authorization blocks; the fix still writes onto the config as it is.
        var view = WithoutHotspotAuthorizationBlocks(live);
        var scope = Scope(view, kind, notes, allowGuestPublicDns);
        var uncovered = scope.Where(n => !Covered(view, kind, n)).ToList();
        if (uncovered.Count == 0) return null;

        var changed = kind == Kind.Dnat
            ? ExtendDnat(view, desired, uncovered, notes)
            : ExtendBlock(view, desired, kind, scope, uncovered, notes, dohDomains ?? DefaultDohDomains());
        return changed ? new DnsFix(desired, notes, TitleOf(kind)) : null;
    }

    /// <summary>
    /// The config without UniFi's own Hotspot blocks (Post-Authorization Restrictions, Block Unauthorized Traffic): they hold
    /// only until a guest is authorized, which the config does not show, and an authorized guest goes on to Allow All Traffic.
    /// Read as blocks, they made a guest network look covered for port 53, DoT, and DoQ but not for DoH (Allow Hotspot Portal
    /// opens 443 first). The DNS audit leaves them out the same way (DnsSecurityAnalyzer.WithoutHotspotAuthorizationBlocks).
    /// </summary>
    private static ConfigState WithoutHotspotAuthorizationBlocks(ConfigState live)
    {
        var hotspot = live.Get("firewall_zone").FirstOrDefault(z => Str(z["zone_key"]) == "hotspot") is { } h ? ConfigState.IdOf(h) : null;
        if (hotspot == null) return live;
        var view = live.Clone();
        view.Collections["firewall_policy"] = live.Get("firewall_policy").Where(p => !(Str(p["predefined"]) is "true" or "True"
            && Str(p["action"]) is "BLOCK" or "REJECT" && Str(p["source"]?["zone_id"]) == hotspot)).ToList();
        return view;
    }

    private static IReadOnlyList<string> DefaultDohDomains() =>
        Audit.Dns.DohProviderRegistry.Providers.Values.SelectMany(p => p.Hostnames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The LAN networks a fix may touch. Out: VPN and WAN networks; the network that holds a LAN resolver (a block
    /// there can cut its upstream; a gateway resolver's upstream leaves from the Gateway zone, so it excludes nothing);
    /// Hotspot and guest networks only when the operator allows them public DNS; and, for port 53, a network whose
    /// DHCP DNS is a public resolver, which a port 53 block would cut off.
    /// </summary>
    private static List<JsonObject> Scope(ConfigState live, Kind kind, List<string> notes, bool allowGuestPublicDns)
    {
        var zoneOf = ZonePaths.ZoneOfNetwork(live);
        var zones = live.Get("firewall_zone").ToDictionary(ConfigState.IdOf, z => Str(z["zone_key"]) ?? "", StringComparer.OrdinalIgnoreCase);
        var networks = live.Get("networkconf").Where(n => Str(n["ip_subnet"]) is { Length: > 0 } && zoneOf.TryGetValue(ConfigState.IdOf(n), out var z)
            && zones.TryGetValue(z, out var key) && key is "" or "internal" or "hotspot" && !Off(n["enabled"])).ToList();
        var gateways = networks.Select(n => Str(n["ip_subnet"])!.Split('/')[0]).ToHashSet();
        bool Lan(string ip) => !gateways.Contains(ip) && networks.Any(n => Readdressing.ParseSubnet(Str(n["ip_subnet"])) is { } s && Readdressing.ParseIp(ip) is { } a && s.Contains(a));
        bool IsGuest(JsonObject n) => zones.GetValueOrDefault(zoneOf[ConfigState.IdOf(n)]) == "hotspot" || Str(n["purpose"]) == "guest";

        var resolverHosts = networks.SelectMany(n => LockoutChecker.Resolvers(live, n)).Where(Lan)
            .Select(ip => networks.FirstOrDefault(n => Readdressing.ParseSubnet(Str(n["ip_subnet"])) is { } s && s.Contains(Readdressing.ParseIp(ip)!.Value)))
            .OfType<JsonObject>().Select(ConfigState.IdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scope = new List<JsonObject>();
        foreach (var n in networks)
        {
            var name = Str(n["name"]) ?? "";
            if (resolverHosts.Contains(ConfigState.IdOf(n)))
            {
                notes.Add($"{name}: holds the DNS resolver, so it is left alone.");
                continue;
            }
            if (IsGuest(n) && allowGuestPublicDns)
            {
                notes.Add($"{name}: a guest network, left alone (Allow Guest / Hotspot Public DNS is on in Settings).");
                continue;
            }
            if (kind == Kind.Port53 && DhcpDns(n).Any(ip => !Lan(ip) && !gateways.Contains(ip)))
            {
                notes.Add($"{name}: hands out a public DNS server, which a port 53 block would cut off; left alone.");
                continue;
            }
            scope.Add(n);
        }
        return scope;
    }

    private static List<string> DhcpDns(JsonObject n) => Str(n["dhcpd_dns_enabled"]) is "true" or "True"
        ? new[] { "dhcpd_dns_1", "dhcpd_dns_2", "dhcpd_dns_3", "dhcpd_dns_4" }.Select(f => Str(n[f])).OfType<string>().Where(s => Readdressing.ParseIp(s) != null).ToList()
        : [];

    private static bool Covered(ConfigState state, Kind kind, JsonObject network)
    {
        var id = ConfigState.IdOf(network);
        // Internet Access off keeps a network off public DNS on every port, DoH included, as the DNS audit counts it (DnsSecurityAnalyzer).
        // UniFi enforces it with its own policy on the network's subnet, which the zone check does not read as the whole network.
        var offline = Off(network["internet_access_enabled"]);
        return kind switch
        {
            Kind.Dnat => Dnats(state).Any(d => Str(d["in_interface"]) == id),
            Kind.Port53 => offline || Dnats(state).Any(d => Str(d["in_interface"]) == id) || ZonePaths.Allows(state, id, Internet, "udp", 53) == false,
            Kind.Dot => offline || ZonePaths.Allows(state, id, Internet, "tcp", 853) == false,
            Kind.Doq => offline || ZonePaths.Allows(state, id, Internet, "udp", 853) == false,
            _ => offline || DohBlocks(state).Any(p => ZonePaths.ZoneOfNetwork(state).GetValueOrDefault(id) == Str(p["source"]?["zone_id"]) && ZonePaths.SourceCovers(p["source"] as JsonObject, id)),
        };
    }

    /// <summary>Enabled DNAT rules that redirect port 53 (the shape <see cref="LockoutChecker.Resolvers"/> reads).</summary>
    private static List<JsonObject> Dnats(ConfigState state) =>
        state.Get("nat").Where(r => Str(r["in_interface"]) is { Length: > 0 } && LockoutChecker.RedirectsDns(state, r)).ToList();

    private static IEnumerable<JsonObject> DohBlocks(ConfigState state)
    {
        var external = state.Get("firewall_zone").FirstOrDefault(z => Str(z["zone_key"]) == "external") is { } e ? ConfigState.IdOf(e) : null;
        return state.Get("firewall_policy").Where(p => Str(p["action"]) is "BLOCK" or "REJECT" && !Off(p["enabled"])
            && Str(p["destination"]?["zone_id"]) == external
            && (Str(p["destination"]?["matching_target"]) == "APP"
                || (Str(p["destination"]?["matching_target"]) == "WEB"
                    && Audit.Dns.DohProviderRegistry.MatchKnownDohDomains((p["destination"]?["web_domains"] as JsonArray ?? []).Select(Str).OfType<string>()).MatchedCount > 0)));
    }

    /// <summary>
    /// Extends the operator's block to the uncovered networks: adds a network to a template that lists networks in
    /// its zone, or copies the template once per zone. A network not covered by any rule gets UniFi's usual shape.
    /// </summary>
    private static bool ExtendBlock(ConfigState live, ConfigState desired, Kind kind, List<JsonObject> scope, List<JsonObject> uncovered,
        List<string> notes, IReadOnlyList<string> dohDomains)
    {
        var def = ConfigCollections.Get("firewall_policy")!;
        var policies = desired.Collections["firewall_policy"];
        var zoneOf = ZonePaths.ZoneOfNetwork(live);
        var names = live.NamesById();
        var native = live.Get("networkconf").FirstOrDefault(n => ConfigCollections.NetworkKey(n) == "vlan:1") is { } nat ? ConfigState.IdOf(nat) : null;

        // The template: a custom block that covers a network in scope today, preferring one not on the native network.
        var templates = scope.Except(uncovered).Select(n => (Net: n, Policy: kind switch
            {
                Kind.Doh => DohBlocks(live).FirstOrDefault(p => zoneOf.GetValueOrDefault(ConfigState.IdOf(n)) == Str(p["source"]?["zone_id"])
                    && ZonePaths.SourceCovers(p["source"] as JsonObject, ConfigState.IdOf(n))),
                Kind.Port53 => ZonePaths.Decides(live, ConfigState.IdOf(n), Internet, "udp", 53),
                Kind.Dot => ZonePaths.Decides(live, ConfigState.IdOf(n), Internet, "tcp", 853),
                _ => ZonePaths.Decides(live, ConfigState.IdOf(n), Internet, "udp", 853),
            }))
            .Where(t => t.Policy != null && Str(t.Policy["action"]) is "BLOCK" or "REJECT" && def.IsGenerated?.Invoke(t.Policy) != true && ConfigCollections.OwnedBy(def, t.Policy) == null)
            .OrderBy(t => ConfigState.IdOf(t.Net) == native)
            .Select(t => t.Policy!).ToList();
        var template = templates.FirstOrDefault();

        var changed = false;
        foreach (var group in uncovered.GroupBy(n => zoneOf[ConfigState.IdOf(n)], StringComparer.OrdinalIgnoreCase))
        {
            var zone = group.Key;
            var ids = group.Select(ConfigState.IdOf).ToList();
            var wholeZone = scope.Where(n => zoneOf[ConfigState.IdOf(n)] == zone).All(n => ids.Contains(ConfigState.IdOf(n)))
                && live.Get("networkconf").Where(n => zoneOf.GetValueOrDefault(ConfigState.IdOf(n)) == zone).All(n => scope.Contains(n));
            var zoneName = names.GetValueOrDefault(zone, "zone");
            if (template != null)
            {
                var src = template["source"] as JsonObject;
                var templateZone = Str(src?["zone_id"]);
                var inPlace = policies.First(p => ConfigState.IdOf(p) == ConfigState.IdOf(template));
                if (templateZone == zone && Str(src?["matching_target"]) == "NETWORK" && !(src?["match_opposite_networks"] is JsonValue o && o.TryGetValue<bool>(out var opp) && opp))
                {
                    var list = (JsonArray)((JsonObject)inPlace["source"]!)["network_ids"]!;
                    foreach (var id in ids.Where(id => !list.Any(x => Str(x) == id))) list.Add(id);
                    notes.Add($"{Str(template["name"])}: now also covers {string.Join(", ", group.Select(n => Str(n["name"])))}.");
                    changed = true;
                    continue;
                }
                if (templateZone == zone)
                {
                    notes.Add($"{string.Join(", ", group.Select(n => Str(n["name"])))}: {Str(template["name"])} is in this zone but an earlier policy lets the traffic through. Check the order in the Edit tab.");
                    continue;
                }
                var copy = (JsonObject)template.DeepClone();
                copy["_id"] = $"fix-{ConfigState.IdOf(template)}-{zone}";
                copy.Remove("index");
                copy["name"] = CopyName(Str(template["name"]) ?? "", names.GetValueOrDefault(templateZone ?? "", ""),
                    live.Get("networkconf").Where(n => zoneOf.GetValueOrDefault(ConfigState.IdOf(n)) == templateZone).Select(n => Str(n["name"])).OfType<string>(), zoneName);
                var source = (JsonObject)copy["source"]!;
                source["zone_id"] = zone;
                SetSource(source, wholeZone, ids);
                policies.Add(copy);
                notes.Add($"{copy["name"]}: copied from {Str(template["name"])} for {zoneName}.");
                changed = true;
                continue;
            }

            var built = Shape(kind, zone, live, dohDomains);
            if (built == null) continue;
            SetSource((JsonObject)built["source"]!, wholeZone, ids);
            if (zoneOf.Values.Distinct().Count() > 1) built["name"] = $"{built["name"]} ({zoneName})";
            built["_id"] = $"fix-new-{zone}";
            policies.Add(built);
            notes.Add($"{built["name"]}: no rule on this site to copy, so UniFi's usual shape is used.");
            changed = true;
        }
        return changed;
    }

    private static void SetSource(JsonObject source, bool wholeZone, List<string> ids)
    {
        foreach (var key in new[] { "network_ids", "ips", "client_macs", "match_opposite_networks", "match_opposite_ips", "matching_target_type" }) source.Remove(key);
        if (wholeZone)
        {
            source["matching_target"] = "ANY";
            return;
        }
        source["matching_target"] = "NETWORK";
        source["network_ids"] = new JsonArray(ids.Select(i => (JsonNode?)i).ToArray());
        source["match_opposite_networks"] = false;
    }

    /// <summary>
    /// A copy's name after the operator's pattern: the template's zone or network name swapped for the new zone's
    /// ("[DNS] Block DoQ / DoT from Home" to "... from Management"), else the zone appended.
    /// </summary>
    internal static string CopyName(string template, string templateZone, IEnumerable<string> templateNetworks, string zone)
    {
        foreach (var name in new[] { templateZone }.Concat(templateNetworks).Where(n => n.Length > 0))
            if (Regex.IsMatch(template, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                return Regex.Replace(template, $@"\b{Regex.Escape(name)}\b", zone.Replace("$", "$$"), RegexOptions.IgnoreCase);
        return $"{template} ({zone})";
    }

    /// <summary>The usual shape for a block with no rule to copy, as UniFi stores one made in its UI.</summary>
    private static JsonObject? Shape(Kind kind, string zone, ConfigState live, IReadOnlyList<string> dohDomains)
    {
        var external = live.Get("firewall_zone").FirstOrDefault(z => Str(z["zone_key"]) == "external") is { } e ? ConfigState.IdOf(e) : null;
        if (external == null) return null;
        var (name, protocol, destination) = kind switch
        {
            Kind.Port53 => ("Block External DNS", "tcp_udp", Dest(external, "53")),
            Kind.Dot or Kind.Doq => ("Block External DNS over TLS / QUIC", "tcp_udp", Dest(external, "853")),
            _ => ("Block External DoH and QUIC", "tcp_udp", DohDest(external, dohDomains)),
        };
        return new JsonObject
        {
            ["name"] = name, ["description"] = "Fix it for me", ["enabled"] = true, ["action"] = "BLOCK", ["ip_version"] = "BOTH",
            ["protocol"] = protocol, ["connection_state_type"] = "ALL", ["connection_states"] = new JsonArray(), ["create_allow_respond"] = false,
            ["logging"] = false, ["match_ip_sec"] = false, ["match_opposite_protocol"] = false, ["icmp_typename"] = "ANY", ["icmp_v6_typename"] = "ANY",
            ["predefined"] = false, ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
            ["source"] = new JsonObject { ["zone_id"] = zone, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false },
            ["destination"] = destination,
        };
    }

    private static JsonObject Dest(string zone, string port) => new()
    {
        ["zone_id"] = zone, ["matching_target"] = "ANY", ["port_matching_type"] = "SPECIFIC", ["port"] = port, ["match_opposite_ports"] = false,
    };

    private static JsonObject DohDest(string zone, IReadOnlyList<string> domains) => new()
    {
        ["zone_id"] = zone, ["matching_target"] = "WEB", ["matching_target_type"] = "SPECIFIC", ["web_matching_type"] = "CUSTOM",
        ["web_domains"] = new JsonArray(domains.Select(d => (JsonNode?)d).ToArray()),
        ["port_matching_type"] = "SPECIFIC", ["port"] = "443", ["match_opposite_ports"] = false,
    };

    /// <summary>
    /// Copies the operator's DNAT once per uncovered network. Every field equal to the template network's ID or its
    /// gateway address becomes the new network's (in_interface, a NETWORK_CONF source, an ADDRESS_AND_PORT exclusion);
    /// the target is the new network's DHCP DNS server when it has one. A redirect into another zone also gets an allow
    /// to the resolver, or it lands on Block All.
    /// </summary>
    private static bool ExtendDnat(ConfigState live, ConfigState desired, List<JsonObject> uncovered, List<string> notes)
    {
        var dnats = Dnats(live);
        if (dnats.Count == 0)
        {
            notes.Add("No DNAT rule on this site to copy; the redirect target is your choice.");
            return false;
        }
        var networks = live.Get("networkconf").ToDictionary(ConfigState.IdOf, n => n, StringComparer.OrdinalIgnoreCase);
        var native = networks.Values.FirstOrDefault(n => ConfigCollections.NetworkKey(n) == "vlan:1") is { } nn ? ConfigState.IdOf(nn) : null;
        // A template whose network is not the native one: there the destination address is both its own gateway
        // and the native gateway, and which one it means cannot be told.
        var template = dnats.OrderBy(d => Str(d["in_interface"]) == native).First();
        var templateNet = networks.GetValueOrDefault(Str(template["in_interface"])!);
        var templateGateway = Str(templateNet?["ip_subnet"])?.Split('/')[0];
        var rules = desired.Collections["nat"];
        var policies = desired.Collections["firewall_policy"];
        var changed = false;
        foreach (var net in uncovered)
        {
            var id = ConfigState.IdOf(net);
            var gateway = Str(net["ip_subnet"])!.Split('/')[0];
            var copy = (JsonObject)Rewrite(template, s => s == Str(template["in_interface"]) ? id : s == templateGateway ? gateway : s)!;
            copy["_id"] = $"fix-dnat-{id}";
            copy.Remove("rule_index");
            if (DhcpDns(net).FirstOrDefault() is { } own && own != gateway) copy["ip_address"] = own;
            copy["description"] = DnatName(Str(template["description"]) ?? "", templateNet, net);
            rules.Add(copy);
            notes.Add($"{copy["description"]}: copied from {Str(template["description"])} for {Str(net["name"])}.");
            changed = true;

            var target = Str(copy["ip_address"])?.Split('-')[0].Trim();
            if (target == null || target == gateway || ZonePaths.Allows(desired, id, target, "udp", 53) != false) continue;
            var zoneOf = ZonePaths.ZoneOfNetwork(desired);
            var (dstZone, _) = ZonePaths.ZoneOfAddress(desired, zoneOf, target);
            if (dstZone == null || !zoneOf.TryGetValue(id, out var srcZone)) continue;
            var names = live.NamesById();
            policies.Add(new JsonObject
            {
                ["_id"] = $"fix-dns-allow-{id}", ["name"] = $"Allow {names.GetValueOrDefault(srcZone, "zone")} to DNS ({target})", ["description"] = "Fix it for me",
                ["enabled"] = true, ["action"] = "ALLOW", ["ip_version"] = "BOTH", ["protocol"] = "tcp_udp", ["connection_state_type"] = "ALL",
                ["connection_states"] = new JsonArray(), ["create_allow_respond"] = true, ["logging"] = false, ["match_ip_sec"] = false,
                ["match_opposite_protocol"] = false, ["icmp_typename"] = "ANY", ["icmp_v6_typename"] = "ANY", ["predefined"] = false,
                ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
                ["source"] = new JsonObject { ["zone_id"] = srcZone, ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray(id), ["match_opposite_networks"] = false, ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false },
                ["destination"] = new JsonObject
                {
                    ["zone_id"] = dstZone, ["matching_target"] = "IP", ["matching_target_type"] = "SPECIFIC", ["ips"] = new JsonArray(target),
                    ["match_opposite_ips"] = false, ["port_matching_type"] = "SPECIFIC", ["port"] = "53", ["match_opposite_ports"] = false,
                },
            });
            notes.Add($"{Str(net["name"])}: the redirect crosses into another zone, so an allow to {target} on port 53 is added.");
        }
        return changed;
    }

    private static JsonNode? Rewrite(JsonNode? node, Func<string, string> map) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Rewrite(p.Value, map)))),
        JsonArray a => new JsonArray(a.Select(e => Rewrite(e, map)).ToArray()),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(map(s)),
        _ => node?.DeepClone(),
    };

    /// <summary>A DNAT copy's name: the template network's name or VLAN swapped for the new one's, else the network appended.</summary>
    internal static string DnatName(string template, JsonObject? from, JsonObject to)
    {
        var toName = Str(to["name"]) ?? "";
        if (Str(from?["name"]) is { Length: > 0 } fromName && Regex.IsMatch(template, $@"\b{Regex.Escape(fromName)}\b", RegexOptions.IgnoreCase))
            return Regex.Replace(template, $@"\b{Regex.Escape(fromName)}\b", toName.Replace("$", "$$"), RegexOptions.IgnoreCase);
        var fromVlan = Str(from?["vlan"]) is { Length: > 0 } v ? v : "1";
        var toVlan = Str(to["vlan"]) is { Length: > 0 } tv ? tv : "1";
        var vlan = Regex.Match(template, $@"\bVLAN\s*{Regex.Escape(fromVlan)}\b", RegexOptions.IgnoreCase);
        if (vlan.Success) return template[..vlan.Index] + $"VLAN {toVlan}" + template[(vlan.Index + vlan.Length)..];
        return $"{template} ({toName})";
    }
}
