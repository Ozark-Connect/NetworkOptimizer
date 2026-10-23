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

        // 3. Inform host override and the console's local DNS name.
        var liveHost = Setting(live, "super_identity", "hostname");
        var plannedHost = Setting(planned, "super_identity", "hostname");
        var overrideOn = Setting(planned, "super_mgmt", "override_inform_host") == "true";
        var settingKey = doc.Operations.FirstOrDefault(o => o.Collection == "setting" && o.ObjectName is "super_identity" or "super_mgmt")?.Key;
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

        // 6. Firewall policies that block traffic into the gateway from any source.
        var gatewayZone = planned.Get("firewall_zone").FirstOrDefault(z => Str(z["zone_key"]) == "gateway");
        if (gatewayZone != null)
            foreach (var op in doc.Operations.Where(o => o.Collection == "firewall_policy" && o.Action is PlanAction.Create or PlanAction.Update))
            {
                var policy = op.Body as JsonObject;
                if (policy == null || Str(policy["enabled"]) == "false") continue;
                if (Str(policy["action"]) is not ("BLOCK" or "REJECT")) continue;
                if (Str(policy["destination"]?["zone_id"]) != ConfigState.IdOf(gatewayZone)) continue;
                if (Str(policy["source"]?["matching_target"]) is not (null or "ANY")) continue;
                findings.Add(new LockoutFinding
                {
                    Kind = "possible",
                    Title = $"Firewall policy {op.ObjectName} blocks traffic to the gateway",
                    Detail = "It matches any source, so it can block Network Optimizer and the UniFi Console from the gateway.",
                    Devices = devicesByMac.Values.Where(d => Str(d[ConfigCollections.DeviceMetaField]?["type"]) is "ugw" or "udm" or "uxg").Select(Name).ToList(),
                    OperationKeys = [op.Key],
                });
            }

        // 7. On an agent site, the agent reaches the server over the WAN: a WAN network change is certain.
        if (options.AgentSite)
            foreach (var op in doc.Operations.Where(o => o.Collection == "networkconf" && o.WanPath && o.Action != PlanAction.Create))
                findings.Add(new LockoutFinding
                {
                    Kind = "certain",
                    Title = $"WAN {op.ObjectName} changes on a site reached through the On-Site Agent",
                    Detail = "The agent reaches Network Optimizer over this WAN. If it breaks, Network Optimizer cannot reach the site to revert.",
                    Devices = devicesByMac.Values.Where(d => Str(d[ConfigCollections.DeviceMetaField]?["type"]) is "ugw" or "udm" or "uxg").Select(Name).ToList(),
                    OperationKeys = [op.Key],
                });

        return findings;
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
