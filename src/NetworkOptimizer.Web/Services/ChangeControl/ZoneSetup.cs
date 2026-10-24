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
    public string Why { get; init; } = "";
    public bool Enabled { get; set; }
    public bool Applicable { get; set; }
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

/// <summary>
/// Set Up Zones (spec "Legacy firewall to ZBF migration, and zone setup"): proposes zones from the
/// Security Audit's network purposes, by trust level or by purpose, lets the user regroup, and turns
/// the board into zone membership plus explicit zone-pair policies placed before UniFi's predefined
/// ones, so the result does not depend on what UniFi generates for a new zone.
/// </summary>
public static class ZoneSetup
{
    public const string TrustMode = "trust";
    public const string PurposeMode = "purpose";
    public const string UnassignedKey = "unassigned";
    private const string NewZonePrefix = "new-zone-";
    private const string NewPolicyPrefix = "new-zone-policy-";

    private static readonly (string Key, ZoneRole Role, string FromName, string Why, bool Default)[] ExceptionDefs =
    [
        ("casting", ZoneRole.IoT, "Casting and mDNS", "Phones cast to TVs and speakers", true),
        ("nvr", ZoneRole.Cameras, "NVR access", "View cameras from home devices", true),
        ("printing", ZoneRole.Printers, "Printing", "Print from home devices", true),
        ("manage", ZoneRole.Management, "Device management", "Reach switch and AP pages from your computers", false),
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
        var zoneOf = new Dictionary<string, string>();
        foreach (var z in liveZones)
            foreach (var id in Ids(z)) zoneOf[id] = Str(z, "name") ?? "";
        // Only LAN networks move; WAN and VPN networks belong to External and Vpn.
        var external = liveZones.Where(z => Str(z, "zone_key") is "external" or "vpn" or "gateway").SelectMany(Ids).ToHashSet();

        var views = networks.Where(n => !external.Contains(n.Id))
            .Select(n => new ZoneNetworkView { Id = n.Id, Name = n.Name, Vlan = n.VlanId, Purpose = n.Purpose.ToString(), CurrentZone = zoneOf.GetValueOrDefault(n.Id) })
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

        // Every trust column appears even when empty, so a network can be dragged into it.
        if (mode == TrustMode)
            foreach (var (key, name, role) in new[] { ("internal", "Internal", ZoneRole.Internal), ("iot", "IoT", ZoneRole.IoT), ("cameras", "Cameras", ZoneRole.Cameras),
                         ("management", "Management", ZoneRole.Management), ("servers", "Servers", ZoneRole.Servers), ("hotspot", "Hotspot", ZoneRole.Hotspot) })
                Zone(key, name, role);
        else
            Zone("internal", "Internal", ZoneRole.Internal);
        foreach (var name in addedZones ?? [])
            Zone("custom-" + name.Trim().ToLowerInvariant().Replace(' ', '-'), name.Trim(), ZoneRole.Custom);
        var unassigned = zones[UnassignedKey] = new ZoneDraft { Key = UnassignedKey, Name = "Unassigned", Role = ZoneRole.Custom, Description = "Stay in their current zone" };

        foreach (var n in networks.Where(n => !external.Contains(n.Id)))
        {
            ZoneDraft target;
            if (moves.TryGetValue(n.Id, out var moved) && zones.TryGetValue(moved, out var chosen)) target = chosen;
            else
            {
                var (role, name, key) = Bucket(n.Purpose, mode);
                target = role == null ? unassigned : Zone(key, name, role.Value);
            }
            target.NetworkIds.Add(n.Id);
        }

        var populated = zones.Values.Where(z => z.Key != UnassignedKey && z.NetworkIds.Count > 0).ToList();
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

        foreach (var z in zones.Values.Where(z => z.Key != UnassignedKey))
            z.Description = Describe(z.Role, exceptionViews);

        var order = new[] { ZoneRole.Internal, ZoneRole.IoT, ZoneRole.Cameras, ZoneRole.Management, ZoneRole.Servers, ZoneRole.Printers, ZoneRole.Dmz, ZoneRole.Hotspot, ZoneRole.Custom };
        return new ZoneSetupView
        {
            Mode = mode,
            LegacyFirewall = IsLegacy(live),
            Networks = views,
            Zones = zones.Values.Where(z => z.Key != UnassignedKey).OrderBy(z => Array.IndexOf(order, z.Role)).ThenBy(z => z.Name).Append(unassigned).ToList(),
            Exceptions = exceptionViews,
        };
    }

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
            ZoneRole.Internal => "Reaches every zone below",
            ZoneRole.IoT => On("casting") ? "Internet only. Internal reaches in" : "Internet only",
            ZoneRole.Cameras => On("nvr") ? "No internet. Internal and NVR reach in" : "No internet",
            ZoneRole.Management => On("manage") ? "Reaches the console and devices. Internal reaches in" : "Reaches the console and devices",
            ZoneRole.Servers => "Internal reaches in. Internet out",
            ZoneRole.Printers => On("printing") ? "Internal prints" : "Isolated",
            ZoneRole.Hotspot => "Internet only",
            ZoneRole.Dmz => "UniFi's DMZ rules",
            _ => "Isolated from other zones",
        };
    }

    /// <summary>The policy between two zones, or null to leave it to UniFi (Hotspot and DMZ keep their own rules).</summary>
    private static (string Action, string Name)? Rule(ZoneDraft a, ZoneDraft b, List<ZoneException> exceptions)
    {
        if (a.Role is ZoneRole.Hotspot or ZoneRole.Dmz || b.Role is ZoneRole.Hotspot or ZoneRole.Dmz) return null;
        if (a.Role == b.Role && a.Role != ZoneRole.Custom) return ("ALLOW", $"Allow {a.Name} to {b.Name}");
        if (a.Role == ZoneRole.Internal)
        {
            if (b.Role == ZoneRole.Servers) return ("ALLOW", $"Allow {a.Name} to {b.Name}");
            var ex = ExceptionDefs.FirstOrDefault(e => e.Role == b.Role);
            if (ex.Key != null && exceptions.Any(e => e.Key == ex.Key && e.Enabled)) return ("ALLOW", ex.FromName);
        }
        return ("BLOCK", $"Block {a.Name} to {b.Name}");
    }

    /// <summary>The desired config for the board: zone membership and the zone-pair policies.</summary>
    public static ConfigState Desired(ConfigState live, ZoneSetupView board)
    {
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
        var active = board.Zones.Where(z => z.Key != UnassignedKey && z.NetworkIds.Count > 0 && ids.ContainsKey(z.Key)).ToList();
        var externalId = live.Get("firewall_zone").FirstOrDefault(z => Str(z, "zone_key") == "external") is { } ext ? ConfigState.IdOf(ext) : null;
        var n = 0;

        void Put(string srcId, string dstId, string action, string name)
        {
            var existing = policies.FirstOrDefault(p => Str(p, "name") == name && p["predefined"]?.GetValueKind() != System.Text.Json.JsonValueKind.True
                && p["source"]?["zone_id"]?.ToString() == srcId && p["destination"]?["zone_id"]?.ToString() == dstId);
            if (existing != null)
            {
                existing["action"] = action;
                existing["enabled"] = true;
                return;
            }
            n++;
            // Shape of a custom zone-pair policy, from sample-firewall-policies-response.txt.
            policies.Add(new JsonObject
            {
                ["_id"] = NewPolicyPrefix + n, ["name"] = name, ["description"] = "Set Up Zones", ["enabled"] = true, ["action"] = action,
                ["index"] = n, ["ip_version"] = "BOTH", ["protocol"] = "all", ["connection_state_type"] = "ALL", ["connection_states"] = new JsonArray(),
                ["create_allow_respond"] = action == "ALLOW", ["logging"] = false, ["match_ip_sec"] = false, ["match_opposite_protocol"] = false,
                ["icmp_typename"] = "ANY", ["icmp_v6_typename"] = "ANY", ["predefined"] = false, ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
                ["source"] = new JsonObject { ["zone_id"] = srcId, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false },
                ["destination"] = new JsonObject { ["zone_id"] = dstId, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY", ["match_opposite_ports"] = false },
            });
        }

        foreach (var a in active)
            foreach (var b in active.Where(b => b != a))
                if (Rule(a, b, board.Exceptions) is { } rule)
                    Put(ids[a.Key], ids[b.Key], rule.Action, rule.Name);
        if (externalId != null)
            foreach (var cams in active.Where(z => z.Role == ZoneRole.Cameras))
                Put(ids[cams.Key], externalId, "BLOCK", $"Block {cams.Name} to Internet");
        return desired;
    }

    /// <summary>Plan options for a zone plan: zones and policies only, nothing deleted.</summary>
    public static PlanOptions Options() => new()
    {
        Collections = new HashSet<string> { "firewall_zone", "firewall_policy" },
        DeleteMissing = false,
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
