using System.Text.Json.Nodes;
using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>What a proposed zone is for; decides the policies between zones.</summary>
public enum ZoneRole { Internal, IoT, Cameras, Management, Servers, Printers, Dmz, Hotspot, Custom }

/// <summary>A LAN network on the zone board.</summary>
public sealed class ZoneNetworkView
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Vlan { get; init; }
    public string Purpose { get; init; } = "";
    public string? CurrentZone { get; init; }
}

/// <summary>A zone column on the board.</summary>
public sealed class ZoneDraft
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public ZoneRole Role { get; init; }
    public string? ExistingId { get; init; }
    public bool BuiltIn { get; init; }

    /// <summary>A custom zone the operator built: its networks stay, and its policies are left as they are.</summary>
    public bool Kept { get; init; }
    public string Description { get; set; } = "";
    public List<string> NetworkIds { get; init; } = [];
    public bool IsNew => ExistingId == null;
}

/// <summary>A named exception: an allow the matrix would otherwise block.</summary>
public sealed class ZoneException
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string FromTo { get; init; } = "";
    public string Why { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Applicable { get; set; }

    /// <summary>The operator's own allow that lands in this pair after the move, which makes the exception unnecessary.</summary>
    public string? CoveredBy { get; set; }
}

/// <summary>One Security Audit acceptance check.</summary>
public sealed record ZoneCheck(string Label, string Status, string Level);

/// <summary>Everything the Set Up Zones board shows.</summary>
public sealed class ZoneSetupView
{
    public string Mode { get; init; } = ZoneSetup.TrustMode;
    public bool LegacyFirewall { get; init; }

    /// <summary>The newest snapshot from before the site moved to zones, when one exists.</summary>
    public int? LegacySnapshotId { get; set; }
    public string? LegacySnapshotLabel { get; set; }
    public List<ZoneNetworkView> Networks { get; init; } = [];
    public List<ZoneDraft> Zones { get; init; } = [];
    public List<ZoneException> Exceptions { get; init; } = [];
    public List<ZoneCheck> Checks { get; set; } = [];
    public bool? ChecksPass { get; set; }
    public int Unassigned => Zones.FirstOrDefault(z => z.Key == ZoneSetup.UnassignedKey)?.NetworkIds.Count ?? 0;
}

/// <summary>The desired config for a board, with what Review Plan should say about it.</summary>
/// <param name="Desired">Zone membership and policies.</param>
/// <param name="Notes">Plan notes: deletes, splits, and what was not carried over.</param>
/// <param name="Rehomed">IDs of operator policies moved to a new zone pair; they write after the zones.</param>
public sealed record ZonePlan(ConfigState Desired, List<string> Notes, HashSet<string> Rehomed);

/// <summary>
/// Set Up Zones (spec "Legacy firewall to ZBF migration, and zone setup"): proposes zones from the
/// Security Audit's network purposes, by trust level or by purpose, lets the user regroup, and turns
/// the board into zone membership plus the policies UniFi's defaults do not already give.
/// </summary>
/// <remarks>
/// UniFi blocks a custom zone from every other LAN zone, both ways, and within itself (UDR7 on
/// Network 11, 2026-10-30), so the plan writes only allows between LAN zones. Moving a network pauses every operator
/// policy that names it and strips the network from it; the plan re-writes those policies, from the copy
/// read before the move, in the same apply.
/// </remarks>
public static class ZoneSetup
{
    public const string TrustMode = "trust";
    public const string PurposeMode = "purpose";
    public const string UnassignedKey = "unassigned";
    private const string NewZonePrefix = "new-zone-";
    private const string NewPolicyPrefix = "new-zone-policy-";
    private const string KeepPrefix = "keep-";

    /// <summary>The port group Allow Internal to Servers uses; the operator opens more by adding to it.</summary>
    public const string ServerServicesGroup = "Server Services";

    /// <summary>
    /// Common corporate and homelab services: web and admin UIs, SSH, RDP, DNS, NTP, SMB, NFS, Proxmox,
    /// Synology DSM, Portainer, Plex, Jellyfin, Home Assistant, MQTT, iperf3.
    /// </summary>
    public static readonly string[] ServerServicesPorts =
        ["80", "443", "8080", "8443", "22", "3389", "53", "123", "445", "111", "2049", "8006", "5000-5001", "9443", "32400", "8096", "8920", "8123", "1883", "8883", "5201"];

    /// <summary>
    /// Each exception allows TCP and UDP to its ports only, which keeps it off the any-to-any audit rule.
    /// Casting: AirPlay (7000, 7100), Google Cast (8008-8009, 8443), Sonos (1400, 1443), Roku (8060), and the
    /// ephemeral range AirPlay 2 streams on. NVR: Protect's viewer ports (443, RTSPS 7441, API 7443, streams
    /// 7445, RTSP 7447); off by default, since Protect on the gateway is reached through the Gateway zone.
    /// Printing: IPP (631), raw (9100), LPD (515). Management: SSH and the device web pages.
    /// </summary>
    private static readonly (string Key, ZoneRole Role, string FromName, string Why, bool Default, string Ports)[] ExceptionDefs =
    [
        ("casting", ZoneRole.IoT, "Casting and mDNS", "Phones cast to TVs and speakers", true, "7000,7100,8008-8009,8443,1400,1443,8060,49152-65535"),
        ("nvr", ZoneRole.Cameras, "NVR access", "Only for an NVR on the camera network; Protect on your gateway needs none", false, "443,7441,7443,7445,7447"),
        ("printing", ZoneRole.Printers, "Printing", "Print from home devices", true, "631,9100,515"),
        ("manage", ZoneRole.Management, "Device management", "Reach switch and AP pages from your computers", false, "22,80,443,8080,8443"),
    ];

    private static (ZoneRole? Role, string Name, string Key) Bucket(NetworkPurpose purpose, string mode) => mode == PurposeMode
        ? purpose switch
        {
            NetworkPurpose.Corporate => (ZoneRole.Internal, "Internal", "internal"),
            NetworkPurpose.Home => (ZoneRole.Internal, "Home", "home"),
            NetworkPurpose.Gaming => (ZoneRole.Internal, "Gaming", "gaming"),
            NetworkPurpose.IoT => (ZoneRole.IoT, "IoT", "iot"),
            NetworkPurpose.Media => (ZoneRole.IoT, "Media", "media"),
            NetworkPurpose.Security => (ZoneRole.Cameras, "Cameras", "cameras"),
            NetworkPurpose.Management or NetworkPurpose.ManagementAccess => (ZoneRole.Management, "Management", "management"),
            NetworkPurpose.Server => (ZoneRole.Servers, "Servers", "servers"),
            NetworkPurpose.Printer => (ZoneRole.Printers, "Printers", "printers"),
            NetworkPurpose.Dmz => (ZoneRole.Dmz, "Dmz", "dmz"),
            NetworkPurpose.Guest => (ZoneRole.Hotspot, "Hotspot", "hotspot"),
            _ => (null, "", UnassignedKey),
        }
        : purpose switch
        {
            NetworkPurpose.Corporate or NetworkPurpose.Home or NetworkPurpose.Gaming => (ZoneRole.Internal, "Internal", "internal"),
            NetworkPurpose.IoT or NetworkPurpose.Media => (ZoneRole.IoT, "IoT", "iot"),
            NetworkPurpose.Security => (ZoneRole.Cameras, "Cameras", "cameras"),
            NetworkPurpose.Management or NetworkPurpose.ManagementAccess => (ZoneRole.Management, "Management", "management"),
            NetworkPurpose.Server => (ZoneRole.Servers, "Servers", "servers"),
            NetworkPurpose.Dmz => (ZoneRole.Dmz, "Dmz", "dmz"),
            NetworkPurpose.Guest => (ZoneRole.Hotspot, "Hotspot", "hotspot"),
            _ => (null, "", UnassignedKey),
        };

    /// <summary>UniFi's built-in zones a role lands in (zone_key, sample-firewall-zones-custom.txt).</summary>
    private static string? BuiltInKey(string key) => key switch { "internal" => "internal", "hotspot" => "hotspot", "dmz" => "dmz", _ => null };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    private static List<string> Ids(JsonObject zone) =>
        (zone["network_ids"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>().ToList();

    /// <summary>Whether the site still runs the legacy firewall (rules and no zones).</summary>
    public static bool IsLegacy(ConfigState live) =>
        live.Get("firewall_zone").Count == 0 && live.Get("firewallrule").Count > 0;

    /// <summary>The board: the proposal for a mode, with the user's moves, added zones, and exception choices applied.</summary>
    /// <param name="live">Live config.</param>
    /// <param name="networks">The Security Audit's networks (purposes detected or overridden).</param>
    /// <param name="mode">Trust or purpose.</param>
    /// <param name="moves">Network ID to zone key, from drag and drop.</param>
    /// <param name="addedZones">Names of zones the user added.</param>
    /// <param name="exceptions">Exception key to on or off; unset ones take their default.</param>
    public static ZoneSetupView Build(ConfigState live, IReadOnlyList<NetworkInfo> networks, string mode,
        IReadOnlyDictionary<string, string>? moves = null, IReadOnlyList<string>? addedZones = null,
        IReadOnlyDictionary<string, bool>? exceptions = null)
    {
        moves ??= new Dictionary<string, string>();
        var liveZones = live.Get("firewall_zone");
        var zoneOf = new Dictionary<string, JsonObject>();
        foreach (var z in liveZones)
            foreach (var id in Ids(z)) zoneOf[id] = z;
        // Only LAN networks move; WAN and VPN networks belong to External and Vpn.
        var external = liveZones.Where(z => Str(z, "zone_key") is "external" or "vpn" or "gateway").SelectMany(Ids).ToHashSet();

        var views = networks.Where(n => !external.Contains(n.Id))
            .Select(n => new ZoneNetworkView { Id = n.Id, Name = n.Name, Vlan = n.VlanId, Purpose = n.Purpose.ToString(), CurrentZone = zoneOf.TryGetValue(n.Id, out var cz) ? Str(cz, "name") : null })
            .OrderBy(n => n.Vlan).ToList();

        var zones = new Dictionary<string, ZoneDraft>();
        ZoneDraft Zone(string key, string name, ZoneRole role)
        {
            if (zones.TryGetValue(key, out var z)) return z;
            var builtIn = BuiltInKey(key);
            var existing = builtIn != null
                ? liveZones.FirstOrDefault(l => Str(l, "zone_key") == builtIn)
                : liveZones.FirstOrDefault(l => string.Equals(Str(l, "name"), name, StringComparison.OrdinalIgnoreCase) && Str(l, "zone_key") is null or "");
            return zones[key] = new ZoneDraft
            {
                Key = key, Name = existing != null ? Str(existing, "name") ?? name : name, Role = role,
                ExistingId = existing != null ? ConfigState.IdOf(existing) : null, BuiltIn = builtIn != null,
            };
        }
        void Keep(JsonObject zone) => zones.TryAdd(KeepPrefix + ConfigState.IdOf(zone), new ZoneDraft
        {
            Key = KeepPrefix + ConfigState.IdOf(zone), Name = Str(zone, "name") ?? "Zone", Role = ZoneRole.Custom,
            ExistingId = ConfigState.IdOf(zone), Kept = true,
        });

        // Every trust column appears even when empty, so a network can be dragged into it.
        if (mode == TrustMode)
            foreach (var (key, name, role) in new[] { ("internal", "Internal", ZoneRole.Internal), ("iot", "IoT", ZoneRole.IoT), ("cameras", "Cameras", ZoneRole.Cameras),
                         ("management", "Management", ZoneRole.Management), ("servers", "Servers", ZoneRole.Servers), ("hotspot", "Hotspot", ZoneRole.Hotspot) })
                Zone(key, name, role);
        else
            Zone("internal", "Internal", ZoneRole.Internal);
        foreach (var name in addedZones ?? [])
            Zone("custom-" + name.Trim().ToLowerInvariant().Replace(' ', '-'), name.Trim(), ZoneRole.Custom);
        // A network already in a zone the operator built stays there; its column is kept so it can be dragged back.
        foreach (var current in networks.Where(n => !external.Contains(n.Id)).Select(n => zoneOf.GetValueOrDefault(n.Id)).OfType<JsonObject>()
                     .Where(ZonePaths.IsCustom).DistinctBy(ConfigState.IdOf))
            if (!zones.Values.Any(z => z.ExistingId == ConfigState.IdOf(current))) Keep(current);
        var unassigned = zones[UnassignedKey] = new ZoneDraft { Key = UnassignedKey, Name = "Unassigned", Role = ZoneRole.Custom, Description = "Stay in their current zone" };

        foreach (var n in networks.Where(n => !external.Contains(n.Id)))
        {
            ZoneDraft target;
            if (moves.TryGetValue(n.Id, out var moved) && zones.TryGetValue(moved, out var chosen)) target = chosen;
            else if (zoneOf.TryGetValue(n.Id, out var current) && ZonePaths.IsCustom(current))
                target = zones.Values.First(z => z.ExistingId == ConfigState.IdOf(current));
            else
            {
                var (role, name, key) = Bucket(n.Purpose, mode);
                target = role == null ? unassigned : Zone(key, name, role.Value);
            }
            target.NetworkIds.Add(n.Id);
        }

        var populated = zones.Values.Where(z => z.Key != UnassignedKey && !z.Kept && z.NetworkIds.Count > 0).ToList();
        var exceptionViews = ExceptionDefs.Select(e =>
        {
            var applicable = populated.Any(z => z.Role == e.Role) && populated.Any(z => z.Role == ZoneRole.Internal);
            return new ZoneException
            {
                Key = e.Key, Name = e.FromName, Why = applicable ? e.Why : $"Only if you assign {RoleNoun(e.Role)} to a zone",
                FromTo = $"Internal to {populated.FirstOrDefault(z => z.Role == e.Role)?.Name ?? RoleNoun(e.Role)}",
                Applicable = applicable, Enabled = applicable && (exceptions?.TryGetValue(e.Key, out var on) == true ? on : e.Default),
            };
        }).ToList();

        var order = new[] { ZoneRole.Internal, ZoneRole.IoT, ZoneRole.Cameras, ZoneRole.Management, ZoneRole.Servers, ZoneRole.Printers, ZoneRole.Dmz, ZoneRole.Hotspot, ZoneRole.Custom };
        var view = new ZoneSetupView
        {
            Mode = mode,
            LegacyFirewall = IsLegacy(live),
            Networks = views,
            Zones = zones.Values.Where(z => z.Key != UnassignedKey).OrderBy(z => Array.IndexOf(order, z.Role)).ThenBy(z => z.Kept).ThenBy(z => z.Name).Append(unassigned).ToList(),
            Exceptions = exceptionViews,
        };

        // An exception the operator's own allow already gives (re-homed into the same pair) starts unticked, unless chosen.
        if (exceptionViews.Any(e => e.Applicable) && live.Collections.ContainsKey("firewall_policy"))
        {
            var preview = Plan(live, new ZoneSetupView { Mode = mode, Zones = view.Zones, Exceptions = exceptionViews.Select(e => new ZoneException { Key = e.Key, Name = e.Name }).ToList() });
            string ZoneId(ZoneDraft d) => d.ExistingId ?? NewZonePrefix + d.Key;
            foreach (var e in exceptionViews.Where(e => e.Applicable))
            {
                var role = ExceptionDefs.First(d => d.Key == e.Key).Role;
                var from = populated.First(z => z.Role == ZoneRole.Internal);
                var to = populated.First(z => z.Role == role);
                if (OperatorAllow(preview.Desired.Get("firewall_policy"), ZoneId(from), ZoneId(to)) is not { } covered) continue;
                e.CoveredBy = Str(covered, "name");
                e.Why = $"Covered by your rule {e.CoveredBy}";
                if (exceptions?.ContainsKey(e.Key) != true) e.Enabled = false;
            }
        }

        foreach (var z in zones.Values.Where(z => z.Key != UnassignedKey))
            z.Description = z.Kept ? "Your zone; its policies stay as they are" : Describe(z.Role, exceptionViews);
        return view;
    }

    /// <summary>An operator allow in a zone pair: enabled, not UniFi's, not one Set Up Zones wrote.</summary>
    private static JsonObject? OperatorAllow(IEnumerable<JsonObject> policies, string src, string dst) =>
        policies.FirstOrDefault(p => Str(p, "action") == "ALLOW" && p["enabled"]?.GetValueKind() != System.Text.Json.JsonValueKind.False
            && p["predefined"]?.GetValueKind() != System.Text.Json.JsonValueKind.True && Str(p, "description") != "Set Up Zones"
            && Str(p["source"]?["zone_id"]) == src && Str(p["destination"]?["zone_id"]) == dst);

    private static string RoleNoun(ZoneRole role) => role switch
    {
        ZoneRole.IoT => "IoT",
        ZoneRole.Cameras => "Cameras",
        ZoneRole.Printers => "Printers",
        ZoneRole.Management => "Management",
        _ => role.ToString(),
    };

    private static string Describe(ZoneRole role, List<ZoneException> exceptions)
    {
        bool On(string key) => exceptions.Any(e => e.Key == key && e.Enabled);
        return role switch
        {
            ZoneRole.Internal => "Reaches the zones you allow",
            ZoneRole.IoT => On("casting") ? "Internet only. Internal reaches in" : "Internet only",
            ZoneRole.Cameras => On("nvr") ? "No internet. Internal reaches the NVR" : "No internet",
            ZoneRole.Management => On("manage") ? "Reaches the console and devices. Internal reaches in" : "Reaches the console and devices",
            ZoneRole.Servers => "Internal reaches its services. Internet out",
            ZoneRole.Printers => On("printing") ? "Internal prints" : "Isolated",
            ZoneRole.Hotspot => "Internet only",
            ZoneRole.Dmz => "UniFi's DMZ rules",
            _ => "Isolated from other zones",
        };
    }

    /// <summary>
    /// The allow between two zones the plan writes, or null where UniFi's default Block All stands (and for
    /// Hotspot and DMZ, which keep their own rules).
    /// </summary>
    private static (string Name, string? Ports, bool ServerGroup)? Allow(ZoneDraft a, ZoneDraft b, List<ZoneException> exceptions)
    {
        if (a.Role is ZoneRole.Hotspot or ZoneRole.Dmz || b.Role is ZoneRole.Hotspot or ZoneRole.Dmz) return null;
        if (a.Role == b.Role && a.Role != ZoneRole.Custom) return ($"Allow {a.Name} to {b.Name}", null, false);
        if (a.Role != ZoneRole.Internal) return null;
        if (b.Role == ZoneRole.Servers) return ($"Allow {a.Name} to {b.Name}", null, true);
        var ex = ExceptionDefs.FirstOrDefault(e => e.Role == b.Role);
        return ex.Key != null && exceptions.Any(e => e.Key == ex.Key && e.Enabled) ? (ex.FromName, ex.Ports, false) : null;
    }

    /// <summary>The desired config for the board: zone membership and the policies it needs.</summary>
    public static ConfigState Desired(ConfigState live, ZoneSetupView board) => Plan(live, board).Desired;

    /// <summary>The desired config for the board, with its plan notes and the operator policies it re-homes.</summary>
    /// <param name="live">Live config, read before any write.</param>
    /// <param name="board">The board.</param>
    /// <param name="informAddresses">Where devices inform to; a zone that would lose that path gets an allow.</param>
    public static ZonePlan Plan(ConfigState live, ZoneSetupView board, IReadOnlyList<string>? informAddresses = null)
    {
        var notes = new List<string>();
        var desired = live.Clone();
        var zones = desired.Collections["firewall_zone"] = desired.Get("firewall_zone").Select(z => (JsonObject)z.DeepClone()).ToList();
        var assigned = board.Zones.Where(z => z.Key != UnassignedKey).SelectMany(z => z.NetworkIds).ToHashSet();
        var ids = new Dictionary<string, string>();

        foreach (var draft in board.Zones.Where(z => z.Key != UnassignedKey && (z.NetworkIds.Count > 0 || !z.IsNew)))
        {
            JsonObject zone;
            if (draft.ExistingId != null)
                zone = zones.First(z => ConfigState.IdOf(z) == draft.ExistingId);
            else
            {
                if (draft.NetworkIds.Count == 0) continue;
                zone = new JsonObject { ["_id"] = NewZonePrefix + draft.Key, ["name"] = draft.Name, ["network_ids"] = new JsonArray() };
                zones.Add(zone);
            }
            ids[draft.Key] = ConfigState.IdOf(zone);
            var keep = Ids(zone).Where(id => !assigned.Contains(id));
            zone["network_ids"] = new JsonArray(keep.Concat(draft.NetworkIds).Distinct().Select(id => (JsonNode?)id).ToArray());
        }
        // A network assigned to a zone leaves every other zone.
        foreach (var zone in zones.Where(z => !ids.ContainsValue(ConfigState.IdOf(z))))
            zone["network_ids"] = new JsonArray(Ids(zone).Where(id => !assigned.Contains(id)).Select(id => (JsonNode?)id).ToArray());

        var policies = desired.Collections["firewall_policy"] = desired.Get("firewall_policy").Select(p => (JsonObject)p.DeepClone()).ToList();
        var oldZoneOf = ZonePaths.ZoneOfNetwork(live);
        var newZoneOf = ZonePaths.ZoneOfNetwork(desired);
        var moved = newZoneOf.Where(p => oldZoneOf.TryGetValue(p.Key, out var was) && !string.Equals(was, p.Value, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = desired.NamesById();
        string ZoneName(string? id) => id != null && names.TryGetValue(id, out var n) ? n : "zone";
        var zoneKey = zones.ToDictionary(ConfigState.IdOf, z => Str(z, "zone_key") ?? "", StringComparer.OrdinalIgnoreCase);
        bool IsLan(string? zone) => zone != null && zoneKey.TryGetValue(zone, out var k) && k is "" or "internal";
        bool IsInternal(string? zone) => zone != null && zoneKey.TryGetValue(zone, out var k) && k == "internal";
        var n = 0;

        var rehomed = Rehome(live, desired, policies, moved, newZoneOf, ZoneName, notes, ref n);
        CopyZoneWide(live, desired, policies, moved, oldZoneOf, newZoneOf, zoneKey, ZoneName, notes, ref n);

        var active = board.Zones.Where(z => z.Key != UnassignedKey && !z.Kept && z.NetworkIds.Count > 0 && ids.ContainsKey(z.Key)).ToList();
        var externalId = zones.FirstOrDefault(z => Str(z, "zone_key") == "external") is { } ext ? ConfigState.IdOf(ext) : null;
        var gatewayId = zones.FirstOrDefault(z => Str(z, "zone_key") == "gateway") is { } gw ? ConfigState.IdOf(gw) : null;

        // A destination limited to ports, in the shape UniFi stores (sample-firewall-policies-response.txt).
        static JsonObject Destination(string zoneId, string? ports, string? groupId = null) => groupId != null
            ? new JsonObject { ["zone_id"] = zoneId, ["matching_target"] = "ANY", ["port_matching_type"] = "OBJECT", ["port_group_id"] = groupId, ["match_opposite_ports"] = false }
            : ports == null
                ? new JsonObject { ["zone_id"] = zoneId, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false }
                : new JsonObject { ["zone_id"] = zoneId, ["matching_target"] = "ANY", ["port_matching_type"] = "SPECIFIC", ["port"] = ports, ["match_opposite_ports"] = false };

        JsonObject Put(string srcId, string dstId, string action, string name, string? ports = null, string protocol = "tcp_udp", string? groupId = null, JsonObject? destination = null)
        {
            var dest = destination ?? Destination(dstId, ports, groupId);
            var specific = ports != null || groupId != null || destination != null;
            var existing = policies.FirstOrDefault(p => Str(p, "name") == name && p["predefined"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
                && Str(p["source"]?["zone_id"]) == srcId && Str(p["destination"]?["zone_id"]) == dstId);
            if (existing != null)
            {
                existing["action"] = action;
                existing["enabled"] = true;
                if (specific)
                {
                    existing["protocol"] = protocol;
                    existing["destination"] = dest;
                }
                return existing;
            }
            // Shape of a custom zone-pair policy, from sample-firewall-policies-response.txt. No index: UniFi
            // ignores it on a create and puts a pair's one custom policy ahead of the predefined ones itself.
            var policy = new JsonObject
            {
                ["_id"] = NewPolicyPrefix + ++n, ["name"] = name, ["description"] = "Set Up Zones", ["enabled"] = true, ["action"] = action,
                ["ip_version"] = "BOTH", ["protocol"] = specific ? protocol : "all", ["connection_state_type"] = "ALL", ["connection_states"] = new JsonArray(),
                ["create_allow_respond"] = action == "ALLOW", ["logging"] = false, ["match_ip_sec"] = false, ["match_opposite_protocol"] = false,
                ["icmp_typename"] = "ANY", ["icmp_v6_typename"] = "ANY", ["predefined"] = false, ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
                ["source"] = new JsonObject { ["zone_id"] = srcId, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false },
                ["destination"] = dest,
            };
            policies.Add(policy);
            return policy;
        }

        // An operator allow already in the pair (re-homed or their own) covers the generic exception.
        JsonObject? Covering(string src, string dst) => OperatorAllow(policies, src, dst);

        foreach (var a in active)
            foreach (var b in active.Where(b => b != a))
            {
                if (Allow(a, b, board.Exceptions) is not { } allow) continue;
                if (ExceptionDefs.Any(e => e.FromName == allow.Name) && Covering(ids[a.Key], ids[b.Key]) is { } covered)
                {
                    notes.Add($"Exception: {allow.Name} ({a.Name} to {b.Name}) is covered by {Str(covered, "name")}; not added.");
                    continue;
                }
                var groupId = allow.ServerGroup ? ServerServicesGroupId(desired, ref n) : null;
                Put(ids[a.Key], ids[b.Key], "ALLOW", allow.Name, allow.Ports, groupId: groupId);
            }

        var networkById = desired.Get("networkconf").ToDictionary(ConfigState.IdOf, x => x, StringComparer.OrdinalIgnoreCase);
        // UniFi blocks networks that share a custom zone from each other: one zone is one trust level.
        // Isolate Network is outbound-only, so never let this allow match an isolated network as its source;
        // leave destinations broad because the setting does not block inbound initiations.
        foreach (var z in active.Where(z => !z.BuiltIn && z.NetworkIds.Count > 1 && z.Role is not (ZoneRole.Hotspot or ZoneRole.Dmz)))
        {
            var isolated = z.NetworkIds.Where(id => networkById.TryGetValue(id, out var net)
                && net["network_isolation_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var included = z.NetworkIds.Where(id => !isolated.Contains(id)).ToList();
            var name = $"Allow {z.Name} within {z.Name}";
            var existing = policies.FirstOrDefault(p => Str(p, "name") == name
                && p["predefined"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
                && Str(p["source"]?["zone_id"]) == ids[z.Key] && Str(p["destination"]?["zone_id"]) == ids[z.Key]);
            // The description survives manual edits. Refresh only source shapes the wizard writes.
            var source = existing?["source"] as JsonObject;
            var wizardSource = source != null && Str(source["matching_target"]) is "ANY" or "NETWORK"
                && Str(source["port_matching_type"]) is null or "ANY"
                && Str(source["matching_target_type"]) != "OBJECT"
                && source["match_opposite_networks"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
                && source["match_opposite_ports"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
                && source["match_mac"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
                && (Str(source["matching_target"]) != "NETWORK"
                    || source["network_ids"] is JsonArray { Count: > 0 } members
                    && members.All(id => newZoneOf.TryGetValue(Str(id) ?? "", out var zone)
                        && string.Equals(zone, ids[z.Key], StringComparison.OrdinalIgnoreCase)));
            if (existing != null && (Str(existing, "description") != "Set Up Zones" || !wizardSource))
            {
                notes.Add($"{name}: the existing operator policy is kept as it is.");
                continue;
            }
            if (included.Count == 0)
            {
                if (existing != null)
                {
                    policies.Remove(existing);
                    notes.Add($"{name}: removed because every network has Isolate Network enabled.");
                }
                continue;
            }

            var policy = Put(ids[z.Key], ids[z.Key], "ALLOW", name);
            // Refresh only the wizard's source restriction; keep any destination or service limits.
            policy["source"] = new JsonObject
            {
                ["zone_id"] = ids[z.Key], ["matching_target"] = isolated.Count > 0 ? "NETWORK" : "ANY",
                ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false,
            };
            if (isolated.Count > 0)
            {
                policy["source"]!["network_ids"] = new JsonArray(included.Select(id => (JsonNode?)id).ToArray());
                notes.Add($"{z.Name}: the within-zone allow excludes networks with Isolate Network enabled.");
            }
        }

        if (externalId != null)
            foreach (var cams in active.Where(z => z.Role == ZoneRole.Cameras))
            {
                // A network with Internet Access off already has UniFi's own block, which the Security Audit honors.
                if (cams.NetworkIds.All(id => networkById.TryGetValue(id, out var net) && net["internet_access_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.False))
                {
                    notes.Add($"{cams.Name}: every network has Internet Access off, so no Block {cams.Name} to Internet is added.");
                    continue;
                }
                Put(ids[cams.Key], externalId, "BLOCK", $"Block {cams.Name} to Internet");
            }

        // The Security Audit's untrusted purposes (IoT, Media, Security, Printer) never need the gateway's SSH or web UI:
        // UniFi devices and Protect cameras use other ports. An operator block that already covers the zone is used instead.
        if (gatewayId != null)
            foreach (var zone in active.Where(z => z.Role is ZoneRole.IoT or ZoneRole.Cameras or ZoneRole.Printers))
            {
                var current = Snapshot(desired, policies);
                var covered = zone.NetworkIds.All(id => networkById.TryGetValue(id, out var net) && Str(net, "ip_subnet")?.Split('/')[0] is { } gwIp
                    && new[] { 22, 80, 443 }.All(port => ZonePaths.Allows(current, id, gwIp, "tcp", port) == false));
                if (covered)
                {
                    notes.Add($"{zone.Name}: the gateway's SSH and web UI are already blocked by an existing policy; no Block {zone.Name} to Gateway Management is added.");
                    continue;
                }
                Put(ids[zone.Key], gatewayId, "BLOCK", $"Block {zone.Name} to Gateway Management", "22,80,443", "tcp");
            }

        KeepPaths(live, desired, policies, moved, ZoneName, informAddresses ?? [], ref n);
        DropRedundantBlocks(policies, rehomed, IsLan, IsInternal, ZoneName, notes);
        return new ZonePlan(desired, notes, rehomed.Keys.Where(id => !id.StartsWith(NewPolicyPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static ConfigState Snapshot(ConfigState desired, List<JsonObject> policies)
    {
        desired.Collections["firewall_policy"] = policies;
        return desired;
    }

    /// <summary>The Server Services port group: the operator's when it exists (never changed), else one the plan creates.</summary>
    private static string ServerServicesGroupId(ConfigState desired, ref int n)
    {
        if (!desired.Collections.TryGetValue("firewallgroup", out var groups)) desired.Collections["firewallgroup"] = groups = [];
        if (groups.FirstOrDefault(g => Str(g, "name") == ServerServicesGroup && Str(g, "group_type") == "port-group") is { } existing)
            return ConfigState.IdOf(existing);
        var group = new JsonObject
        {
            ["_id"] = NewPolicyPrefix + "group-" + ++n, ["name"] = ServerServicesGroup, ["group_type"] = "port-group",
            ["group_members"] = new JsonArray(ServerServicesPorts.Select(p => (JsonNode?)p).ToArray()),
        };
        groups.Add(group);
        return ConfigState.IdOf(group);
    }

    /// <summary>The networks a policy side names: by network ID, or by an address inside a network's subnet. Client and ANY sides name none.</summary>
    private static List<string> Named(JsonObject? side, IReadOnlyList<JsonObject> networks)
    {
        if (side == null) return [];
        switch (Str(side["matching_target"]))
        {
            case "NETWORK" when side["match_opposite_networks"]?.GetValueKind() != System.Text.Json.JsonValueKind.True:
                return (side["network_ids"] as JsonArray ?? []).Select(Str).OfType<string>().ToList();
            case "IP" when side["match_opposite_ips"]?.GetValueKind() != System.Text.Json.JsonValueKind.True:
                return (side["ips"] as JsonArray ?? []).Select(Str).OfType<string>()
                    .Select(e => NetworkOfEntry(e, networks)).OfType<string>().Distinct().ToList();
            default:
                return [];
        }
    }

    private static string? NetworkOfEntry(string entry, IReadOnlyList<JsonObject> networks)
    {
        var first = entry.Split('/')[0].Split('-')[0].Trim();
        if (Readdressing.ParseIp(first) is not { } ip) return null;
        return networks.FirstOrDefault(n => Readdressing.ParseSubnet(Str(n, "ip_subnet")) is { } s && s.Contains(ip)) is { } net ? ConfigState.IdOf(net) : null;
    }

    /// <summary>
    /// Operator policies naming a moved network, moved to the zone pair it now sits in. UniFi pauses them and strips
    /// the network on the zone write; the update re-writes them whole from the policy read before. A policy whose
    /// networks land in different zones is split, one per pair. A client side names no network: it follows its clients'
    /// network when they have fixed IPs, else keeps its zone. UniFi pauses such a policy and strips the network side but
    /// leaves its zones as they were (UDR7 on Network 11, 2026-10-30). Returns policy ID to its name, for the redundancy pass.
    /// </summary>
    private static Dictionary<string, string> Rehome(ConfigState live, ConfigState desired, List<JsonObject> policies, HashSet<string> moved,
        Dictionary<string, string> newZoneOf, Func<string?, string> zoneName, List<string> notes, ref int n)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (moved.Count == 0) return result;
        var networks = live.Get("networkconf");
        var users = live.Get("user").Where(u => Str(u, "mac") != null)
            .GroupBy(u => Str(u, "mac")!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var def = ConfigCollections.Get("firewall_policy")!;
        foreach (var policy in policies.ToList())
        {
            if (def.IsGenerated?.Invoke(policy) == true || ConfigCollections.OwnedBy(def, policy) != null) continue;
            var src = policy["source"] as JsonObject;
            var dst = policy["destination"] as JsonObject;
            // "Every network but these": a moved network has left the side's zone, so its exclusion is dropped (the side
            // still matches what it did). UniFi pauses the policy on the zone write; the update re-writes it whole.
            var excluded = DropMovedExclusions(src, moved) | DropMovedExclusions(dst, moved);
            if (excluded)
                notes.Add($"Firewall Policy {Str(policy, "name")}: excludes a network that moves out of its zone, so that exclusion is dropped.");
            // A client side follows its clients' network when the config knows it (a fixed IP) and they all land in one zone.
            var clientsUnknown = false;
            var clientsMoved = FollowClients(policy, src, users, moved, newZoneOf, zoneName, notes, ref clientsUnknown)
                | FollowClients(policy, dst, users, moved, newZoneOf, zoneName, notes, ref clientsUnknown);
            var srcNets = Named(src, networks);
            var dstNets = Named(dst, networks);
            if (!srcNets.Any(moved.Contains) && !dstNets.Any(moved.Contains))
            {
                if (excluded || clientsMoved) result[ConfigState.IdOf(policy)] = Str(policy, "name") ?? "";
                continue;
            }
            if (clientsUnknown)
                notes.Add($"Firewall Policy {Str(policy, "name")}: matches clients without a fixed IP, so their side keeps its zone. Check it once the networks move if those clients are on a network that moves.");

            // Each side's networks grouped by the zone they now sit in; a side naming no moved network keeps its zone.
            List<(string Zone, List<string> Nets)> Groups(JsonObject? side, List<string> nets) =>
                nets.Any(moved.Contains)
                    ? nets.GroupBy(id => newZoneOf.GetValueOrDefault(id) ?? Str(side?["zone_id"]) ?? "").Select(g => (g.Key, g.ToList())).ToList()
                    : [(Str(side?["zone_id"]) ?? "", nets)];
            var variants = Groups(src, srcNets).SelectMany(s => Groups(dst, dstNets).Select(d => (Src: s, Dst: d))).ToList();
            // The original keeps the variant on its own zones when there is one; the rest are new policies.
            var keepIndex = Math.Max(0, variants.FindIndex(v => v.Src.Zone == Str(src?["zone_id"]) && v.Dst.Zone == Str(dst?["zone_id"])));
            var original = (JsonObject)policy.DeepClone();
            for (var i = 0; i < variants.Count; i++)
            {
                var (s, d) = variants[i];
                var target = i == keepIndex ? policy : (JsonObject)original.DeepClone();
                Narrow(target["source"] as JsonObject, s.Zone, s.Nets, srcNets, networks);
                Narrow(target["destination"] as JsonObject, d.Zone, d.Nets, dstNets, networks);
                if (i != keepIndex)
                {
                    target["_id"] = NewPolicyPrefix + ++n;
                    target["name"] = $"{Str(policy, "name")} ({zoneName(s.Zone != Str(src?["zone_id"]) ? s.Zone : d.Zone)})";
                    policies.Add(target);
                }
                result[ConfigState.IdOf(target)] = Str(target, "name") ?? "";
            }
            if (variants.Count > 1)
                notes.Add($"Firewall Policy {Str(policy, "name")}: its networks now sit in {variants.Count} zone pairs, so it is split into {variants.Count} policies.");
        }
        return result;
    }

    /// <summary>
    /// A client side moved to the zone its clients' network now sits in. The config knows a client's network only from
    /// its fixed IP; a side with clients on networks landing in different zones keeps its zone and gets a note. Sets
    /// <paramref name="unknown"/> when a client's network is not known. Returns whether the side moved.
    /// </summary>
    private static bool FollowClients(JsonObject policy, JsonObject? side, Dictionary<string, JsonObject> users, HashSet<string> moved,
        Dictionary<string, string> newZoneOf, Func<string?, string> zoneName, List<string> notes, ref bool unknown)
    {
        if (side == null || Str(side["matching_target"]) is not ("CLIENT" or "MAC")) return false;
        var macs = (side["client_macs"] as JsonArray ?? []).Concat(side["macs"] as JsonArray ?? []).Select(Str).OfType<string>().ToList();
        var nets = macs.Select(mac => users.TryGetValue(mac, out var u) && Readdressing.IsTrue(u["use_fixedip"])
            && Str(u, "network_id") is { Length: > 0 } id ? id : null).ToList();
        if (!nets.Any(id => id != null && moved.Contains(id)))
        {
            if (nets.Count == 0 || nets.Contains(null)) unknown = true;
            return false;
        }
        var zones = nets.Select(id => id == null ? null : newZoneOf.GetValueOrDefault(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (zones is [{ } zone])
        {
            side["zone_id"] = zone;
            notes.Add($"Firewall Policy {Str(policy, "name")}: its clients' network moves to {zoneName(zone)}, so their side moves with it.");
            return true;
        }
        notes.Add($"Firewall Policy {Str(policy, "name")}: matches clients whose networks land in different zones (or have no fixed IP), so their side keeps {zoneName(Str(side["zone_id"]))}. Split it by hand in the Edit tab.");
        return false;
    }

    /// <summary>
    /// Moved networks taken out of a "every network but these" side. A side left with no exclusions matches any network.
    /// Returns whether the side changed.
    /// </summary>
    private static bool DropMovedExclusions(JsonObject? side, HashSet<string> moved)
    {
        if (side?["match_opposite_networks"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
            || side["network_ids"] is not JsonArray ids || !ids.Any(id => Str(id) is { } x && moved.Contains(x))) return false;
        var keep = ids.Select(Str).Where(id => id != null && !moved.Contains(id)).Select(id => (JsonNode?)id).ToArray();
        if (keep.Length > 0)
        {
            side["network_ids"] = new JsonArray(keep);
            return true;
        }
        side["matching_target"] = "ANY";
        foreach (var key in new[] { "network_ids", "match_opposite_networks", "matching_target_type" }) side.Remove(key);
        return true;
    }

    /// <summary>A side set to one zone and only the networks (or addresses) that sit there.</summary>
    private static void Narrow(JsonObject? side, string zone, List<string> keep, List<string> all, IReadOnlyList<JsonObject> networks)
    {
        if (side == null) return;
        side["zone_id"] = zone;
        if (keep.Count == all.Count) return;
        if (side["network_ids"] is JsonArray ids)
            side["network_ids"] = new JsonArray(ids.Select(Str).Where(id => id != null && keep.Contains(id)).Select(id => (JsonNode?)id).ToArray());
        if (side["ips"] is JsonArray ips)
            side["ips"] = new JsonArray(ips.Select(Str).Where(e => e != null && NetworkOfEntry(e, networks) is { } net && keep.Contains(net)).Select(e => (JsonNode?)e).ToArray());
    }

    /// <summary>
    /// Zone-wide policies (source ANY) of a zone that loses networks stop applying to them. Blocks and allows out to
    /// External or the Gateway, and allows to a DNS resolver, are copied to each new zone in order: UniFi allows a custom
    /// zone everything out, so the protection is lost otherwise. Network Optimizer's own blocklists are never copied:
    /// Threat Intelligence - Blocking writes one per zone itself. Other allows to LAN zones are not carried, so the
    /// new zone stays isolated; each is named in a note.
    /// </summary>
    private static void CopyZoneWide(ConfigState live, ConfigState desired, List<JsonObject> policies, HashSet<string> moved, Dictionary<string, string> oldZoneOf,
        Dictionary<string, string> newZoneOf, Dictionary<string, string> zoneKey, Func<string?, string> zoneName, List<string> notes, ref int n)
    {
        var def = ConfigCollections.Get("firewall_policy")!;
        var groups = live.Get("firewallgroup");
        var lanTargets = moved.GroupBy(id => oldZoneOf[id], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(id => newZoneOf[id]).Where(z => zoneKey.TryGetValue(z, out var k) && k is "" or "internal")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var networksById = desired.Get("networkconf").ToDictionary(ConfigState.IdOf, StringComparer.OrdinalIgnoreCase);
        // Zone-wide: ANY, or everyone but a few hosts ([UniFi] Allow Select Console Management's shape).
        static bool ZoneWide(JsonObject? side) => Str(side?["matching_target"]) is null or "ANY"
            || (Str(side?["matching_target"]) == "IP" && side?["match_opposite_ips"]?.GetValueKind() == System.Text.Json.JsonValueKind.True);
        var templates = live.Get("firewall_policy")
            .Where(p => def.IsGenerated?.Invoke(p) != true && p["enabled"]?.GetValueKind() != System.Text.Json.JsonValueKind.False
                        && ZoneWide(p["source"] as JsonObject) && Str(p["source"]?["zone_id"]) is { } z && lanTargets.ContainsKey(z))
            .OrderBy(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue).ToList();
        bool InternetDisabled(string zone)
        {
            var members = newZoneOf.Where(pair => pair.Value == zone).Select(pair => pair.Key).ToList();
            return members.Count > 0 && members.All(id => networksById.TryGetValue(id, out var net)
                && net["internet_access_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.False);
        }
        foreach (var t in templates)
        {
            var from = Str(t["source"]?["zone_id"])!;
            var dstZone = Str(t["destination"]?["zone_id"]);
            var dstKey = dstZone != null ? zoneKey.GetValueOrDefault(dstZone) : null;
            var name = Str(t, "name") ?? "";
            if (ConfigCollections.OwnedBy(def, t) != null)
            {
                var owned = $"{name}: managed by Threat Intelligence, which writes one per zone on its next sync.";
                if (!notes.Contains(owned)) notes.Add(owned);
                continue;
            }
            var outward = dstKey is "external" or "gateway";
            var resolver = Str(t, "action") == "ALLOW" && dstKey is "" or "internal" && CoversPort(t["destination"] as JsonObject, 53, groups);
            foreach (var to in lanTargets[from])
            {
                if (dstKey == "external" && (Str(t, "action") is "BLOCK" or "REJECT") && InternetDisabled(to))
                {
                    var skipped = $"{name}: not copied to {zoneName(to)}; every network there already has Internet Access off.";
                    if (!notes.Contains(skipped)) notes.Add(skipped);
                    continue;
                }
                if (outward || (resolver && !string.Equals(to, dstZone, StringComparison.OrdinalIgnoreCase)))
                {
                    var copyName = NameFor(name, zoneName(from), zoneName(to));
                    if (policies.Any(p => Str(p, "name") == copyName && Str(p["source"]?["zone_id"]) == to)) continue;
                    var copy = (JsonObject)t.DeepClone();
                    copy["_id"] = NewPolicyPrefix + ++n;
                    copy["name"] = copyName;
                    ((JsonObject)copy["source"]!)["zone_id"] = to;
                    policies.Add(copy);
                }
                else if (Str(t, "action") == "ALLOW" && dstKey is "" or "internal")
                    notes.Add($"Firewall Policy {name} ({zoneName(from)} to {zoneName(dstZone)}): not carried to {zoneName(to)}, which stays isolated. Copy it in the Edit tab if {zoneName(to)} needs it.");
            }
        }
    }

    /// <summary>A copy's name: the source zone's name swapped in when the template names it ("from Home" to "from IoT"), else the zone appended.</summary>
    internal static string NameFor(string template, string fromZone, string toZone) =>
        template.Contains(fromZone, StringComparison.OrdinalIgnoreCase)
            ? System.Text.RegularExpressions.Regex.Replace(template, System.Text.RegularExpressions.Regex.Escape(fromZone), toZone.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : $"{template} ({toZone})";

    private static bool CoversPort(JsonObject? side, int port, IReadOnlyList<JsonObject> groups)
    {
        if (side == null) return false;
        return Str(side["port_matching_type"]) switch
        {
            "SPECIFIC" => Str(side["port"]) is { } list && ZonePaths.PortListContains(list, port),
            "OBJECT" => groups.FirstOrDefault(g => ConfigState.IdOf(g) == Str(side["port_group_id"])) is { } g
                        && ZonePaths.PortListContains(string.Join(",", (g["group_members"] as JsonArray ?? []).Select(Str)), port),
            _ => false,
        };
    }

    /// <summary>
    /// Each moved network keeps reaching its DNS resolver and the inform host: where the planned zones block a path
    /// that works today, an allow on just that path is added.
    /// </summary>
    private static void KeepPaths(ConfigState live, ConfigState desired, List<JsonObject> policies, HashSet<string> moved,
        Func<string?, string> zoneName, IReadOnlyList<string> informAddresses, ref int n)
    {
        var current = Snapshot(desired, policies);
        var zoneOf = ZonePaths.ZoneOfNetwork(current);
        var defaultLan = live.Get("networkconf").FirstOrDefault(x => ConfigCollections.NetworkKey(x) == "vlan:1");
        var deviceNetworks = live.Get("device").Where(d => Str(d[ConfigCollections.DeviceMetaField]?["type"]) is not ("ugw" or "udm" or "uxg"))
            .Select(d => Str(d["mgmt_network_id"]) is { Length: > 0 } m ? m : defaultLan == null ? null : ConfigState.IdOf(defaultLan))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var counter = n;
        void AllowTo(string network, string address, string ports, string label)
        {
            if (!zoneOf.TryGetValue(network, out var src)) return;
            var (dst, _) = ZonePaths.ZoneOfAddress(current, zoneOf, address);
            if (dst == null) return;
            var name = $"Allow {zoneName(src)} to {label} ({address})";
            if (!added.Add(name) || policies.Any(p => Str(p, "name") == name)) return;
            policies.Add(new JsonObject
            {
                ["_id"] = NewPolicyPrefix + ++counter, ["name"] = name, ["description"] = "Set Up Zones", ["enabled"] = true, ["action"] = "ALLOW",
                ["ip_version"] = "BOTH", ["protocol"] = "tcp_udp", ["connection_state_type"] = "ALL", ["connection_states"] = new JsonArray(),
                ["create_allow_respond"] = true, ["logging"] = false, ["match_ip_sec"] = false, ["match_opposite_protocol"] = false,
                ["icmp_typename"] = "ANY", ["icmp_v6_typename"] = "ANY", ["predefined"] = false, ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
                ["source"] = new JsonObject { ["zone_id"] = src, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false },
                ["destination"] = new JsonObject
                {
                    ["zone_id"] = dst, ["matching_target"] = "IP", ["matching_target_type"] = "SPECIFIC", ["ips"] = new JsonArray(address),
                    ["match_opposite_ips"] = false, ["port_matching_type"] = "SPECIFIC", ["port"] = ports, ["match_opposite_ports"] = false,
                },
            });
        }
        foreach (var network in moved)
        {
            var liveNet = live.Get("networkconf").FirstOrDefault(x => ConfigState.IdOf(x) == network);
            foreach (var resolver in LockoutChecker.Resolvers(live, liveNet))
                if (ZonePaths.Allows(live, network, resolver, "udp", 53) == true && ZonePaths.Allows(current, network, resolver, "udp", 53) == false)
                    AllowTo(network, resolver, "53", "DNS");
            if (!deviceNetworks.Contains(network)) continue;
            // Inform (TCP 8080) and STUN (UDP 3478), from UniFi's published port list.
            foreach (var host in informAddresses)
                if (ZonePaths.Allows(live, network, host, "tcp", 8080) == true && ZonePaths.Allows(current, network, host, "tcp", 8080) == false)
                    AllowTo(network, host, "8080,3478", "UniFi Console");
        }
        n = counter;
    }

    /// <summary>
    /// A block a re-home puts on a pair UniFi already blocks (two different LAN zones, at least one custom, or within a
    /// custom zone) with no allow in that pair for it to order against, is deleted: Block All does the same.
    /// </summary>
    private static void DropRedundantBlocks(List<JsonObject> policies, Dictionary<string, string> rehomed, Func<string?, bool> isLan,
        Func<string?, bool> isInternal, Func<string?, string> zoneName, List<string> notes)
    {
        bool Custom(string? zone) => isLan(zone) && !isInternal(zone);
        // Also the zone-pair blocks an earlier Set Up Zones wrote before it relied on UniFi's defaults.
        foreach (var p in policies.Where(p => (rehomed.ContainsKey(ConfigState.IdOf(p)) || Str(p, "description") == "Set Up Zones")
                     && Str(p, "action") is "BLOCK" or "REJECT").ToList())
        {
            var src = Str(p["source"]?["zone_id"]);
            var dst = Str(p["destination"]?["zone_id"]);
            if (!isLan(src) || !isLan(dst) || !(Custom(src) || Custom(dst))) continue;
            if (policies.Any(o => o != p && Str(o, "action") == "ALLOW" && o["enabled"]?.GetValueKind() != System.Text.Json.JsonValueKind.False
                    && Str(o["source"]?["zone_id"]) == src && Str(o["destination"]?["zone_id"]) == dst)) continue;
            policies.Remove(p);
            notes.Add($"Firewall Policy {Str(p, "name")}: deleted. UniFi already blocks {zoneName(src)} to {zoneName(dst)} by default.");
        }
    }

    /// <summary>Plan options for a zone plan: zones, policies, and the Server Services group. Deletes are only the redundant blocks the plan drops.</summary>
    public static PlanOptions Options() => new()
    {
        Collections = new HashSet<string> { "firewall_zone", "firewall_policy", "firewallgroup" },
        DeleteMissing = true,
    };

    /// <summary>The Security Audit acceptance checks (spec: the audit's isolation rules must not fire).</summary>
    public static (List<ZoneCheck> Checks, bool Pass) Checks(ZoneSetupView board, IReadOnlyList<NetworkInfo> networks, ImpactResult? after)
    {
        var checks = new List<ZoneCheck>();
        if (after == null)
        {
            checks.Add(new("Security Audit", "Not run: the UniFi Console did not answer", "warn"));
            return (checks, false);
        }
        var names = networks.ToDictionary(n => n.Id, n => n.Name);
        bool Fires(params string[] types) => after.AfterIssues.Any(i => types.Contains(i.Type));
        var populated = board.Zones.Where(z => z.Key != ZoneSetup.UnassignedKey && z.NetworkIds.Count > 0).ToList();

        void Add(string label, bool fires) => checks.Add(new(label, fires ? "Fails" : "Pass", fires ? "error" : "ok"));
        if (populated.Any(z => z.Role == ZoneRole.IoT))
            Add("IoT networks isolated from Internal", Fires(Audit.IssueTypes.IotNetworkNotIsolated, Audit.IssueTypes.MediaNetworkNotIsolated));
        if (populated.Any(z => z.Role == ZoneRole.Cameras))
            Add("Cameras have no internet", Fires(Audit.IssueTypes.SecurityNetworkNotIsolated));
        if (populated.Any(z => z.Role == ZoneRole.Management))
            Add("Management isolated", Fires(Audit.IssueTypes.MgmtNetworkNotIsolated));
        if (populated.FirstOrDefault(z => z.Role == ZoneRole.Hotspot) is { } hotspot)
        {
            var guest = hotspot.NetworkIds.Select(id => names.GetValueOrDefault(id)).OfType<string>().ToHashSet();
            Add("Guest reaches internet only", after.AfterIssues.Any(i => i.Type == Audit.IssueTypes.MissingIsolation && i.Network != null && guest.Contains(i.Network)));
        }
        var unassigned = board.Zones.First(z => z.Key == ZoneSetup.UnassignedKey).NetworkIds.Select(id => names.GetValueOrDefault(id)).OfType<string>().ToList();
        if (unassigned.Count > 0)
            checks.Add(new($"{string.Join(" and ", unassigned)} stay in their current zone", "Not checked", "warn"));
        var guestNames = populated.Where(z => z.Role == ZoneRole.Hotspot).SelectMany(z => z.NetworkIds).Select(id => names.GetValueOrDefault(id)).OfType<string>().ToHashSet();
        var other = after.AfterIssues.Where(i => i.Type is Audit.IssueTypes.MissingIsolation or Audit.IssueTypes.IsolationBypassed).Select(i => i.Network).OfType<string>()
            .Where(n => !unassigned.Contains(n) && !guestNames.Contains(n)).Distinct().ToList();
        if (other.Count > 0) Add($"Isolation on {string.Join(", ", other)}", true);
        return (checks, checks.All(c => c.Level != "error"));
    }
}
