using System.Net;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// The lockout pre-flight (spec "Lockout pre-flight"), run on the planned state against live:
/// uplink ports still carry each downstream device's management VLAN; static device IPs stay in
/// their management subnet; the inform host and the console's local DNS still resolve; DHCP stays
/// on where devices use it; management subnets; firewall policies that could block Network
/// Optimizer from the gateway; and, on agent sites, the agent's own WAN path. Only what the plan
/// changes is reported.
/// </summary>
[VendorSpecific("UniFi", "port_overrides tagged_vlan_mgmt / native_networkconf_id / excluded_networkconf_ids; super_mgmt inform override")]
public static class LockoutChecker
{
    public static List<LockoutFinding> Check(ConfigState live, ConfigState planned, PlanDocument doc, PlanOptions options)
    {
        var findings = new List<LockoutFinding>();
        var deviceOps = doc.Operations.Where(o => o.Collection == "device").ToDictionary(o => o.ObjectId ?? o.Key, o => o.Key);
        string? OpFor(string collection, string? id) =>
            doc.Operations.FirstOrDefault(o => o.Collection == collection && string.Equals(o.ObjectId, id, StringComparison.OrdinalIgnoreCase))?.Key;

        var defaultLan = planned.Get("networkconf").FirstOrDefault(n => ConfigCollections.NetworkKey(n) == "vlan:1");
        string? MgmtNetwork(JsonObject device) =>
            Str(device["mgmt_network_id"]) is { Length: > 0 } m ? m : defaultLan == null ? null : ConfigState.IdOf(defaultLan);

        var devicesByMac = planned.Get("device").Where(d => Str(d["mac"]) != null)
            .GroupBy(d => Str(d["mac"])!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var liveDevicesByMac = live.Get("device").Where(d => Str(d["mac"]) != null)
            .GroupBy(d => Str(d["mac"])!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());

        // 1. Uplink ports carry each device's management network.
        foreach (var (mac, device) in devicesByMac)
        {
            var meta = device[ConfigCollections.DeviceMetaField] as JsonObject;
            if (Str(meta?["type"]) is "ugw" or "udm" or "uxg") continue;
            var uplinkMac = Str(meta?["uplink"]?["uplink_mac"])?.ToLowerInvariant();
            var uplinkPort = Str(meta?["uplink"]?["uplink_remote_port"]);
            if (uplinkMac == null || uplinkPort == null || !devicesByMac.TryGetValue(uplinkMac, out var uplink)) continue;
            var mgmt = MgmtNetwork(device);
            if (mgmt == null) continue;

            var before = liveDevicesByMac.TryGetValue(uplinkMac, out var liveUplink) ? Carries(live, liveUplink, uplinkPort, mgmt) : true;
            var after = Carries(planned, uplink, uplinkPort, mgmt);
            if (before != true || after == true) continue;

            var affected = new List<string> { Name(device) };
            affected.AddRange(Downstream(devicesByMac, mac).Select(Name));
            var keys = new List<string>();
            if (OpFor("device", ConfigState.IdOf(uplink)) is { } k) keys.Add(k);
            var profileId = PortOverride(uplink, uplinkPort)?["portconf_id"];
            if (OpFor("portconf", Str(profileId)) is { } pk) keys.Add(pk);
            keys.AddRange(doc.Operations.Where(o => o.Collection == "portconf" && o.ManagementPath).Select(o => o.Key));
            findings.Add(new LockoutFinding
            {
                Kind = after == false ? "certain" : "possible",
                Title = $"{Name(device)} may lose its management path",
                Detail = $"{Name(uplink)} port {uplinkPort} {(after == false ? "drops" : "may drop")} the {planned.NamesById().GetValueOrDefault(mgmt, "management")} network. "
                         + $"{Name(device)} is managed over that port{(affected.Count > 1 ? $", and so are {string.Join(", ", affected.Skip(1))}" : "")}.",
                Devices = affected.Distinct().ToList(),
                OperationKeys = keys.Distinct().ToList(),
            });
        }

        // 2. Static device IPs stay inside their management network.
        foreach (var (mac, device) in devicesByMac)
        {
            if (!liveDevicesByMac.TryGetValue(mac, out var liveDevice)) continue;
            if (CanonicalJson.DeepEquals(device["config_network"], liveDevice["config_network"])
                && Str(device["mgmt_network_id"]) == Str(liveDevice["mgmt_network_id"])) continue;
            var cn = device["config_network"] as JsonObject;
            var key = OpFor("device", ConfigState.IdOf(device));
            var mgmt = MgmtNetwork(device);
            var subnet = Str(planned.Get("networkconf").FirstOrDefault(n => ConfigState.IdOf(n) == mgmt)?["ip_subnet"]);
            var certain = Str(cn?["type"]) == "static" && Str(cn?["ip"]) is { } ip && subnet != null && !InSubnet(ip, subnet);
            findings.Add(new LockoutFinding
            {
                Kind = certain ? "certain" : "possible",
                Title = $"{Name(device)} IP settings change",
                Detail = certain
                    ? $"The static address {Str(cn?["ip"])} is outside its management network {subnet}. The device would not reach the UniFi Console."
                    : "A device network override can leave the device without an address on its management network.",
                Devices = [Name(device)],
                OperationKeys = key == null ? [] : [key],
            });
        }

        // 3. Inform host override and the console's local DNS name. The host is super_mgmt's
        // override_inform_host_location; super_identity.hostname is the console's OS hostname.
        var liveHost = Setting(live, "super_mgmt", "override_inform_host_location");
        var plannedHost = Setting(planned, "super_mgmt", "override_inform_host_location");
        var overrideOn = Setting(planned, "super_mgmt", "override_inform_host") == "true";
        var settingKey = doc.Operations.FirstOrDefault(o => o.Collection == "setting" && o.ObjectName == "super_mgmt")?.Key;
        if (overrideOn && (liveHost != plannedHost || Setting(live, "super_mgmt", "override_inform_host") != "true"))
            findings.Add(new LockoutFinding
            {
                Kind = string.IsNullOrWhiteSpace(plannedHost) ? "certain" : "possible",
                Title = "Inform Host Override changes",
                Detail = $"Every device will inform to {(string.IsNullOrWhiteSpace(plannedHost) ? "an empty host" : plannedHost)}. A wrong host takes every device offline.",
                Devices = devicesByMac.Values.Select(Name).ToList(),
                OperationKeys = settingKey == null ? [] : [settingKey],
            });
        var consoleNames = new[] { plannedHost, liveHost, options.ConsoleHost }.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h!.ToLowerInvariant()).ToHashSet();
        foreach (var op in doc.Operations.Where(o => o.Collection == "static_dns"))
        {
            var record = op.Body as JsonObject ?? live.Get("static_dns").FirstOrDefault(r => ConfigState.IdOf(r) == op.ObjectId);
            var host = Str(record?["key"])?.ToLowerInvariant();
            if (host == null || !consoleNames.Contains(host)) continue;
            findings.Add(new LockoutFinding
            {
                Kind = "possible",
                Title = $"Local DNS for {host} changes",
                Detail = "Devices resolve the UniFi Console by this name. A record that points elsewhere takes them offline.",
                Devices = devicesByMac.Values.Select(Name).ToList(),
                OperationKeys = [op.Key],
            });
        }

        // 4 and 5. DHCP and subnet on management networks.
        foreach (var network in planned.Get("networkconf"))
        {
            var id = ConfigState.IdOf(network);
            var liveNet = live.Get("networkconf").FirstOrDefault(n => ConfigState.IdOf(n) == id);
            if (liveNet == null) continue;
            var onIt = devicesByMac.Values.Where(d => MgmtNetwork(d) == id).ToList();
            if (onIt.Count == 0) continue;
            var key = OpFor("networkconf", id);
            if (Str(liveNet["dhcpd_enabled"]) == "true" && Str(network["dhcpd_enabled"]) == "false")
            {
                var dhcpDevices = onIt.Where(d => Str(d["config_network"]?["type"]) is null or "dhcp").Select(Name).ToList();
                if (dhcpDevices.Count > 0)
                    findings.Add(new LockoutFinding
                    {
                        Kind = "possible",
                        Title = $"DHCP turns off on {ConfigState.DisplayName(ConfigCollections.Get("networkconf"), network)}",
                        Detail = "Devices on it use DHCP. They lose their address at the next renewal, and new devices cannot adopt.",
                        Devices = dhcpDevices,
                        OperationKeys = key == null ? [] : [key],
                    });
            }
            if (Str(liveNet["ip_subnet"]) != Str(network["ip_subnet"]) || Str(liveNet["vlan"]) != Str(network["vlan"]))
                findings.Add(new LockoutFinding
                {
                    Kind = "possible",
                    Title = $"Management network {ConfigState.DisplayName(ConfigCollections.Get("networkconf"), network)} changes its subnet or VLAN",
                    Detail = "Devices managed on it drop until they renew, and devices with static addresses may not come back.",
                    Devices = onIt.Select(Name).ToList(),
                    OperationKeys = key == null ? [] : [key],
                });
        }

        // Networks Network Optimizer reaches the site from (the agent host's, or the server's own).
        var gateways = devicesByMac.Values.Where(d => Str(d[ConfigCollections.DeviceMetaField]?["type"]) is "ugw" or "udm" or "uxg").Select(Name).ToList();
        // IPv4 only: InSubnet answers true for what it cannot compare, which would match every network.
        // The routed source only for a zone-membership change; every other plan (Re-address) keeps every host address.
        var zonePlan = doc.Operations.Any(o => o.Collection == "firewall_zone" && o.Action != PlanAction.Delete);
        var noAddresses = (zonePlan && options.ReachAddresses.Count > 0 ? options.ReachAddresses : options.ManagementAddresses)
            .Where(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .ToList();
        var managementNetworks = planned.Get("networkconf")
            .Where(n => Str(n["ip_subnet"]) is { } s && s.Contains('/') && noAddresses.Any(a => InSubnet(a, s)))
            .Select(ConfigState.IdOf).ToHashSet();
        var managementZones = planned.Get("firewall_zone")
            .Where(z => (z["network_ids"] as JsonArray ?? []).Any(n => managementNetworks.Contains(Str(n) ?? "")))
            .Select(ConfigState.IdOf).ToHashSet();

        // 6. Firewall policies that block traffic into the gateway from any source, or from where
        //    Network Optimizer reaches the site.
        var gatewayZone = planned.Get("firewall_zone").FirstOrDefault(z => Str(z["zone_key"]) == "gateway");
        // A zone this plan creates holds only the networks the plan puts in it, so with Network Optimizer's
        // and the console's addresses known, an ANY source there is provably not where we manage from.
        var consoleNetworks = IPAddress.TryParse(options.ConsoleHost, out var consoleIp) && consoleIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? planned.Get("networkconf").Where(n => Str(n["ip_subnet"]) is { } s && s.Contains('/') && InSubnet(consoleIp.ToString(), s)).Select(ConfigState.IdOf).ToHashSet()
            : [];
        var createdZones = doc.Operations.Where(o => o.Collection == "firewall_zone" && o.Action == PlanAction.Create)
            .Select(o => PlanBuilder.RefPrefix + o.Key).ToHashSet(StringComparer.Ordinal);
        bool UnmanagedNewZone(string? zoneId) => noAddresses.Count > 0 && zoneId != null && createdZones.Contains(zoneId)
            && planned.Get("firewall_zone").FirstOrDefault(z => ConfigState.IdOf(z) == zoneId) is { } zone
            && !(zone["network_ids"] as JsonArray ?? []).Any(n => managementNetworks.Contains(Str(n) ?? "") || consoleNetworks.Contains(Str(n) ?? ""));
        if (gatewayZone != null)
            foreach (var op in doc.Operations.Where(o => o.Collection == "firewall_policy" && o.Action is PlanAction.Create or PlanAction.Update))
            {
                var policy = op.Body as JsonObject;
                if (policy == null || Str(policy["enabled"]) == "false") continue;
                if (Str(policy["action"]) is not ("BLOCK" or "REJECT")) continue;
                if (Str(policy["destination"]?["zone_id"]) != ConfigState.IdOf(gatewayZone)) continue;
                // A block aimed at SNMP or another unrelated service on the gateway cannot cut off
                // the console or Network Optimizer. Treat unknown protocol/port shapes conservatively.
                if (!CouldBlockManagementTraffic(policy, planned)) continue;
                var source = policy["source"] as JsonObject;
                var target = Str(source?["matching_target"]);
                var fromAny = target is null or "ANY";
                var fromNo = target switch
                {
                    "NETWORK" => (source?["network_ids"] as JsonArray ?? []).Any(n => managementNetworks.Contains(Str(n) ?? "")),
                    "IP" => (source?["ips"] as JsonArray ?? []).Select(Str).OfType<string>()
                        .Any(ip => noAddresses.Any(a => ip == a || (ip.Contains('/') && InSubnet(a, ip)))),
                    _ => false,
                };
                var fromNoZone = fromAny && managementZones.Contains(Str(source?["zone_id"]) ?? "");
                if (!fromAny && !fromNo) continue;
                if (fromAny && !fromNoZone && UnmanagedNewZone(Str(source?["zone_id"]))) continue;
                findings.Add(new LockoutFinding
                {
                    Kind = fromNo || fromNoZone ? (options.AgentSite ? "certain" : "possible") : "possible",
                    Title = $"Firewall Policy {op.ObjectName} blocks traffic to the gateway",
                    Detail = fromNo || fromNoZone
                        ? "It matches where Network Optimizer reaches the site from, so Network Optimizer can lose the gateway and the UniFi Console."
                        : "It matches any source, so it can block Network Optimizer and the UniFi Console from the gateway.",
                    Devices = gateways,
                    OperationKeys = [op.Key],
                });
            }

        static bool CouldBlockManagementTraffic(JsonObject policy, ConfigState state)
        {
            var protocol = Str(policy["protocol"]);
            if (protocol is not (null or "all" or "tcp" or "TCP UDP" or "tcp_udp" or "TCP_UDP")) return false;
            var destination = policy["destination"] as JsonObject;
            // An inverted list matches every port except those named; do not mistake it for a narrow block.
            if (destination?["match_opposite_ports"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) return true;
            var portType = Str(destination?["port_matching_type"]);
            if (portType is null or "ANY") return true;
            IEnumerable<int> ports;
            if (portType == "SPECIFIC" && Str(destination?["port"]) is { } list)
                ports = list.Split(',').SelectMany(part =>
                {
                    var range = part.Trim().Split('-', 2);
                    if (!int.TryParse(range[0], out var first)) return [];
                    var last = range.Length == 2 && int.TryParse(range[1], out var end) ? end : first;
                    return Enumerable.Range(first, Math.Max(0, last - first + 1));
                });
            else if (portType == "OBJECT" && Str(destination?["port_group_id"]) is { } groupId
                     && state.Get("firewallgroup").FirstOrDefault(g => ConfigState.IdOf(g) == groupId) is { } group)
                ports = (group["group_members"] as JsonArray ?? []).Select(Str).OfType<string>().SelectMany(value =>
                {
                    var range = value.Trim().Split('-', 2);
                    if (!int.TryParse(range[0], out var first)) return [];
                    var last = range.Length == 2 && int.TryParse(range[1], out var end) ? end : first;
                    return Enumerable.Range(first, Math.Max(0, last - first + 1));
                });
            else return true;
            return ports.Any(port => port is 22 or 80 or 443 or 8080 or 8443);
        }

        // 7. On an agent site, the agent reaches the server over the WAN and the console over its own
        //    network: a change to either is certain.
        if (options.AgentSite)
        {
            foreach (var op in doc.Operations.Where(o => o.Collection == "networkconf" && o.WanPath && o.Action != PlanAction.Create))
                findings.Add(new LockoutFinding
                {
                    Kind = "certain",
                    Title = $"WAN {op.ObjectName} changes on a site reached through the On-Site Agent",
                    Detail = "The agent reaches Network Optimizer over this WAN. If it breaks, Network Optimizer cannot reach the site to revert.",
                    Devices = gateways,
                    OperationKeys = [op.Key],
                });
            foreach (var op in doc.Operations.Where(o => o.Collection == "networkconf" && o.Action != PlanAction.Create && managementNetworks.Contains(o.ObjectId ?? "")))
            {
                var liveNet = live.Get("networkconf").FirstOrDefault(n => ConfigState.IdOf(n) == op.ObjectId);
                var plannedNet = planned.Get("networkconf").FirstOrDefault(n => ConfigState.IdOf(n) == op.ObjectId);
                if (plannedNet != null && liveNet != null && Str(liveNet["ip_subnet"]) == Str(plannedNet["ip_subnet"])
                    && Str(liveNet["vlan"]) == Str(plannedNet["vlan"]) && Str(liveNet["dhcpd_enabled"]) == Str(plannedNet["dhcpd_enabled"])
                    && Str(liveNet["enabled"]) == Str(plannedNet["enabled"]))
                    continue;
                findings.Add(new LockoutFinding
                {
                    Kind = "certain",
                    Title = $"The On-Site Agent's network {op.ObjectName} changes",
                    Detail = "The agent host is on this network. A changed subnet, VLAN, or DHCP can cut it off from the UniFi Console and from Network Optimizer.",
                    Devices = gateways,
                    OperationKeys = [op.Key],
                });
            }
        }

        // 8. Zone-based firewall paths: a network that reaches its DNS resolver or the inform host today
        //    still does. Devices stay online when DNS breaks, so the commit-confirmed revert never sees it.
        //    Not across a subnet move (Re-address): live and planned addresses differ, so the paths cannot be compared.
        var subnetMoves = ImpactAnalyzer.SubnetMoves(live, planned).Count > 0;
        if (!subnetMoves && doc.Operations.Any(o => o.Collection is "firewall_zone" or "firewall_policy" or "nat")
            && planned.Collections.ContainsKey("firewall_zone") && planned.Get("firewall_zone").Count > 0)
            findings.AddRange(PathFindings(live, planned, doc, options, devicesByMac.Values, MgmtNetwork));

        return findings;
    }

    private static IEnumerable<LockoutFinding> PathFindings(ConfigState live, ConfigState planned, PlanDocument doc, PlanOptions options,
        IEnumerable<JsonObject> devices, Func<JsonObject, string?> mgmtNetwork)
    {
        var names = planned.NamesById();
        var plannedZones = ZonePaths.ZoneOfNetwork(planned);
        var lanZones = planned.Get("firewall_zone").Where(z => ZonePaths.IsCustom(z) || Str(z["zone_key"]) is "internal" or "hotspot" or "dmz")
            .Select(ConfigState.IdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The zone write that moves the network, else the policies and redirects on its zone.
        List<string> Keys(string network)
        {
            var moves = doc.Operations.Where(o => o.Collection == "firewall_zone"
                && (o.Body?["network_ids"] as JsonArray ?? []).Any(n => string.Equals(Str(n), network, StringComparison.OrdinalIgnoreCase))).Select(o => o.Key).ToList();
            if (moves.Count > 0) return moves;
            var zone = plannedZones.GetValueOrDefault(network);
            return doc.Operations.Where(o => (o.Collection == "firewall_policy" && string.Equals(Str(o.Body?["source"]?["zone_id"]), zone, StringComparison.OrdinalIgnoreCase))
                                             || (o.Collection == "nat" && Str(o.Body?["in_interface"]) == network))
                .Select(o => o.Key).ToList();
        }
        string Zone(string? id) => id == null ? "another zone" : names.GetValueOrDefault(id, "another zone");

        foreach (var net in planned.Get("networkconf"))
        {
            var id = ConfigState.IdOf(net);
            if (!plannedZones.TryGetValue(id, out var zone) || !lanZones.Contains(zone)) continue;
            foreach (var resolver in Resolvers(planned, net).Union(Resolvers(live, live.Get("networkconf").FirstOrDefault(n => ConfigState.IdOf(n) == id))).Distinct())
            {
                if (ZonePaths.Allows(live, id, resolver, "udp", 53) != true || ZonePaths.Allows(planned, id, resolver, "udp", 53) != false) continue;
                var (dstZone, _) = ZonePaths.ZoneOfAddress(planned, plannedZones, resolver);
                yield return new LockoutFinding
                {
                    Kind = "possible",
                    Title = $"DNS stops resolving on {names.GetValueOrDefault(id, id)}",
                    Detail = $"{names.GetValueOrDefault(id, id)} sends DNS to {resolver}, and the planned firewall blocks {Zone(zone)} to {Zone(dstZone)} on port 53. "
                             + "Devices stay online when DNS fails, so the automatic revert does not catch it.",
                    Devices = [],
                    OperationKeys = Keys(id),
                };
            }
        }

        if (options.InformAddresses.Count == 0) yield break;
        var byNetwork = devices.Where(d => Str(d[ConfigCollections.DeviceMetaField]?["type"]) is not ("ugw" or "udm" or "uxg"))
            .GroupBy(d => mgmtNetwork(d)).Where(g => g.Key != null);
        foreach (var group in byNetwork)
            foreach (var host in options.InformAddresses)
            {
                if (ZonePaths.Allows(live, group.Key!, host, "tcp", 8080) != true || ZonePaths.Allows(planned, group.Key!, host, "tcp", 8080) != false) continue;
                yield return new LockoutFinding
                {
                    Kind = "certain",
                    Title = $"Devices on {names.GetValueOrDefault(group.Key!, group.Key!)} lose the UniFi Console",
                    Detail = $"They inform to {host} on TCP 8080, and the planned firewall blocks that path. They go offline and cannot take a revert.",
                    Devices = group.Select(Name).ToList(),
                    OperationKeys = Keys(group.Key!),
                };
            }
    }

    /// <summary>
    /// Where a network's DNS goes: the target of a DNAT that redirects its port 53, else its DHCP DNS servers,
    /// else its own gateway address.
    /// </summary>
    internal static List<string> Resolvers(ConfigState state, JsonObject? network)
    {
        if (network == null) return [];
        var id = ConfigState.IdOf(network);
        var dnat = state.Get("nat").Where(r => Str(r["in_interface"]) == id && RedirectsDns(state, r))
            .Select(r => Str(r["ip_address"])?.Split('-')[0].Trim()).OfType<string>().Where(a => a.Length > 0).ToList();
        if (dnat.Count > 0) return dnat;
        if (Str(network["dhcpd_dns_enabled"]) == "true")
        {
            var servers = new[] { "dhcpd_dns_1", "dhcpd_dns_2", "dhcpd_dns_3", "dhcpd_dns_4" }
                .Select(f => Str(network[f])).OfType<string>().Where(s => Readdressing.ParseIp(s) != null).ToList();
            if (servers.Count > 0) return servers;
        }
        return Str(network["ip_subnet"])?.Split('/')[0] is { Length: > 0 } gw ? [gw] : [];
    }

    /// <summary>An enabled DNAT whose destination filter takes port 53: a port, a port group holding 53, or no port at all.</summary>
    internal static bool RedirectsDns(ConfigState state, JsonObject rule)
    {
        if (Str(rule["type"]) != "DNAT" || Str(rule["enabled"]) == "false") return false;
        if (rule["destination_filter"] is not JsonObject filter) return true;
        if (Str(filter["port"]) is { Length: > 0 } port) return ZonePaths.PortListContains(port, 53);
        var groups = state.Get("firewallgroup");
        var portGroups = (filter["firewall_group_ids"] as JsonArray ?? []).Select(Str)
            .Select(g => groups.FirstOrDefault(x => ConfigState.IdOf(x) == g)).OfType<JsonObject>()
            .Where(g => Str(g["group_type"]) == "port-group").ToList();
        return portGroups.Count == 0 || portGroups.Any(g => ZonePaths.PortListContains(string.Join(",", (g["group_members"] as JsonArray ?? []).Select(Str)), 53));
    }

    private static string? Str(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        _ => n.ToString(),
    };

    private static string Name(JsonObject device) =>
        Str(device["name"]) is { Length: > 0 } n ? n : Str(device[ConfigCollections.DeviceMetaField]?["model"]) ?? Str(device["mac"]) ?? "device";

    private static string? Setting(ConfigState state, string key, string field) =>
        Str(state.Get("setting").FirstOrDefault(s => Str(s["key"]) == key)?[field]);

    private static JsonObject? PortOverride(JsonObject device, string portIdx) =>
        (device["port_overrides"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => Str(p["port_idx"]) == portIdx);

    /// <summary>
    /// Whether a switch port carries a network: true, false, or null when it cannot be told
    /// (a profile created by this plan, an unknown mode).
    /// </summary>
    public static bool? Carries(ConfigState state, JsonObject device, string portIdx, string networkId)
    {
        var ovr = PortOverride(device, portIdx);
        if (ovr == null) return true;
        JsonObject? profile = null;
        if (Str(ovr["portconf_id"]) is { Length: > 0 } pid)
        {
            if (pid.StartsWith(PlanBuilder.RefPrefix, StringComparison.Ordinal)) return null;
            profile = state.Get("portconf").FirstOrDefault(p => ConfigState.IdOf(p) == pid);
            if (profile == null) return null;
        }
        JsonNode? Field(string name) => ovr.ContainsKey(name) ? ovr[name] : profile?[name];

        var forward = Str(Field("forward"));
        if (forward == "disabled") return false;
        if (Str(Field("native_networkconf_id")) == networkId) return true;
        var excluded = (Field("excluded_networkconf_ids") as JsonArray)?.Select(Str).ToHashSet() ?? [];
        var tagged = (Field("tagged_networkconf_ids") as JsonArray)?.Select(Str).ToHashSet();
        return Str(Field("tagged_vlan_mgmt")) switch
        {
            "block_all" => false,
            "auto" or "custom" => !excluded.Contains(networkId),
            _ => forward switch
            {
                "native" => false,
                "all" or null => !excluded.Contains(networkId),
                "customize" => tagged?.Contains(networkId) ?? !excluded.Contains(networkId),
                _ => null,
            },
        };
    }

    private static IEnumerable<JsonObject> Downstream(Dictionary<string, JsonObject> devices, string mac)
    {
        var children = devices.Values.Where(d => Str(d[ConfigCollections.DeviceMetaField]?["uplink"]?["uplink_mac"])?.ToLowerInvariant() == mac).ToList();
        foreach (var child in children)
        {
            yield return child;
            foreach (var grand in Downstream(devices, Str(child["mac"])!.ToLowerInvariant()))
                yield return grand;
        }
    }

    /// <summary>True when an IPv4 address is inside a CIDR ("192.168.1.1/24").</summary>
    public static bool InSubnet(string ip, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(ip, out var addr) || !IPAddress.TryParse(parts[0], out var net)
            || !int.TryParse(parts[1], out var bits) || addr.AddressFamily != net.AddressFamily)
            return true;
        var a = addr.GetAddressBytes();
        var n = net.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}
