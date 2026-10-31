using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One impact line: an audit issue a change added (regression) or removed (fix).</summary>
public sealed class ImpactLine
{
    /// <summary>"Regression" or "Fix".</summary>
    public string Kind { get; set; } = "Regression";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>The audit issue type (IssueTypes), for checks that match on the rule rather than its title.</summary>
    public string Type { get; set; } = "";
    public string? Network { get; set; }
    public string? Device { get; set; }
}

/// <summary>The result of a before/after audit.</summary>
public sealed class ImpactResult
{
    public List<ImpactLine> Regressions { get; } = [];
    public List<ImpactLine> Fixes { get; } = [];
    public int BeforeIssueCount { get; set; }
    public int AfterIssueCount { get; set; }
    public List<ImpactLine> AfterIssues { get; } = [];

    /// <summary>The highest severity among the regressions, or null.</summary>
    public AuditSeverity? WorstRegression { get; set; }
}

/// <summary>
/// Builds the Security Audit's config inputs from a <see cref="ConfigState"/> over live telemetry,
/// and turns two audit results into impact lines (spec "Impact diff").
/// </summary>
[VendorSpecific("UniFi", "overlays snapshot device config onto live stat/device telemetry")]
public static class ImpactAnalyzer
{
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

    /// <summary>True when any changed collection feeds the audit.</summary>
    public static bool Triggers(IEnumerable<string> collections) =>
        collections.Any(c => AuditConfigInputs.TriggerCollections.Contains(c));

    /// <summary>
    /// Config inputs for one state. <paramref name="liveDevicesJson"/> is stat/device as read now;
    /// <paramref name="liveGroups"/> is rest/firewallgroup as read now, for the members of Network Optimizer's own groups.
    /// </summary>
    /// <param name="moves">
    /// Subnets this state moves from live (<see cref="SubnetMoves"/>). What live or UniFi still holds at the old
    /// addresses (client and device IPs, generated policies, Network Optimizer's own groups) is audited at the new ones.
    /// </param>
    public static AuditConfigInputs BuildInputs(ConfigState state, string liveDevicesJson, IReadOnlyList<UniFiFirewallGroup>? liveGroups = null,
        IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)>? moves = null)
    {
        if (moves is { Count: > 0 }) state = MoveGenerated(state, moves);
        state = WithZoneMembership(state);
        // A zone the plan creates has no predefined policies until UniFi generates them on create.
        state = ZonePaths.WithCustomZoneDefaults(state);
        return new AuditConfigInputs
        {
            DeviceDataJson = OverlayDevices(state, liveDevicesJson, moves),
            MapClientIp = moves is { Count: > 0 } ? ip => MapAddress(ip, moves) : null,
            SettingsData = Element(new JsonObject { ["meta"] = new JsonObject { ["rc"] = "ok" }, ["data"] = Array(state.Get("setting")) }),
            FirewallPoliciesData = state.Collections.ContainsKey("firewall_policy") ? Element(Array(state.Get("firewall_policy"))) : null,
            LegacyFirewallRulesData = state.Collections.ContainsKey("firewallrule") ? Element(Array(state.Get("firewallrule"))) : null,
            FirewallGroups = FirewallGroups(state, liveGroups, moves),
            NatRulesData = state.Collections.ContainsKey("nat") ? Element(Array(state.Get("nat"))) : null,
            PortProfiles = Typed<UniFiPortProfile>(state, "portconf"),
            NetworkConfigs = Typed<UniFiNetworkConfig>(state, "networkconf"),
            FirewallZones = Typed<UniFiFirewallZone>(state, "firewall_zone"),
            PortForwardRules = Typed<UniFiPortForwardRule>(state, "portforward"),
            UpnpEnabled = state.Get("setting").FirstOrDefault(s => s["key"]?.ToString() == "usg")?["upnp_enabled"] is JsonValue v
                          && v.TryGetValue<bool>(out var b) ? b : null,
        };
    }

    /// <summary>
    /// Each network's firewall_zone_id as the planned zones list it. The audit reads a network's zone from
    /// that field, which a plan moving networks between zones (Set Up Zones, an edit of a zone's networks)
    /// does not change: IoT moved to a new zone was audited in Internal, and its block rules never applied.
    /// </summary>
    internal static ConfigState WithZoneMembership(ConfigState state)
    {
        if (!state.Collections.ContainsKey("firewall_zone") || !state.Collections.ContainsKey("networkconf")) return state;
        var zoneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in state.Get("firewall_zone"))
            foreach (var id in (zone["network_ids"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>())
                zoneOf[id] = ConfigState.IdOf(zone);
        var moved = state.Get("networkconf").Where(n => zoneOf.TryGetValue(ConfigState.IdOf(n), out var z) && n["firewall_zone_id"]?.ToString() != z).ToList();
        if (moved.Count == 0) return state;
        var copy = state.Clone();
        foreach (var n in copy.Get("networkconf"))
            if (zoneOf.TryGetValue(ConfigState.IdOf(n), out var z)) n["firewall_zone_id"] = z;
        return copy;
    }

    /// <summary>Networks whose subnet differs between two states, matched by ID, as (old, new) pairs.</summary>
    public static List<(Readdressing.Subnet Old, Readdressing.Subnet New)> SubnetMoves(ConfigState from, ConfigState to)
    {
        var old = from.Get("networkconf").Where(n => n["_id"] != null)
            .GroupBy(ConfigState.IdOf).ToDictionary(g => g.Key, g => g.First()["ip_subnet"]?.ToString());
        var moves = new List<(Readdressing.Subnet, Readdressing.Subnet)>();
        foreach (var n in to.Get("networkconf"))
        {
            if (!old.TryGetValue(ConfigState.IdOf(n), out var was)) continue;
            if (Readdressing.ParseSubnet(was) is { } o && Readdressing.ParseSubnet(n["ip_subnet"]?.ToString()) is { } m && o != m)
                moves.Add((o, m));
        }
        return moves;
    }

    private static string? MapAddress(string? ip, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> moves) =>
        Readdressing.ParseIp(ip) is { } a && Readdressing.MapIp(a, moves) is { } m ? Readdressing.Format(m) : null;

    /// <summary>
    /// UniFi regenerates its own objects from the network (the policy a network with Internet Access off gets), so a
    /// plan leaves them at the old addresses. They are audited as UniFi will regenerate them.
    /// </summary>
    private static ConfigState MoveGenerated(ConfigState state, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> moves)
    {
        var moved = state.Clone();
        foreach (var (key, list) in moved.Collections)
        {
            if (ConfigCollections.Get(key)?.IsGenerated is not { } generated) continue;
            foreach (var obj in list.Where(o => generated(o))) RewriteStrings(obj, moves);
        }
        return moved;
    }

    private static void RewriteStrings(JsonNode? node, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> moves)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) { if (Readdressing.Rewrite(s, moves) is { } r) o[k] = r; }
                    else RewriteStrings(v, moves);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s)) { if (Readdressing.Rewrite(s, moves) is { } r) a[i] = r; }
                    else RewriteStrings(a[i], moves);
                break;
        }
    }

    private static JsonArray Array(IEnumerable<JsonObject> objects) =>
        new(objects.Select(o => (JsonNode?)o.DeepClone()).ToArray());

    private static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static List<T>? Typed<T>(ConfigState state, string collection)
    {
        if (!state.Collections.ContainsKey(collection)) return null;
        var list = new List<T>();
        foreach (var o in state.Get(collection))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(o.ToJsonString(), Lenient) is { } item) list.Add(item);
            }
            catch (JsonException)
            {
                // An object the typed model cannot hold is left out of the audit, not fatal.
            }
        }
        return list;
    }

    /// <summary>
    /// The state's firewall groups. A snapshot keeps Network Optimizer's own groups as shells (their members are
    /// generated), so those take their members from live: without them, every rule using one reads as any-to-any.
    /// </summary>
    private static List<UniFiFirewallGroup>? FirewallGroups(ConfigState state, IReadOnlyList<UniFiFirewallGroup>? liveGroups,
        IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)>? moves = null)
    {
        var groups = Typed<UniFiFirewallGroup>(state, "firewallgroup");
        if (groups == null || liveGroups == null) return groups;
        var byId = liveGroups.Where(g => g.Id.Length > 0).GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        var byName = liveGroups.GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var group in groups.Where(g => g.Name.StartsWith(ConfigCollections.NoOwnedPrefix, StringComparison.Ordinal)))
        {
            if ((byId.GetValueOrDefault(group.Id) ?? byName.GetValueOrDefault(group.Name)) is { } live)
                group.GroupMembers = moves is { Count: > 0 }
                    ? [.. live.GroupMembers.Select(m => Readdressing.Rewrite(m, moves) ?? m)]
                    : [.. live.GroupMembers];
        }
        return groups;
    }

    /// <summary>Live stat/device with each device's config fields replaced by the state's.</summary>
    public static string OverlayDevices(ConfigState state, string liveDevicesJson,
        IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)>? moves = null)
    {
        var live = ConfigSnapshotReader.ExtractList(liveDevicesJson) ?? [];
        if (moves is { Count: > 0 })
            foreach (var device in live)
                if (MapAddress(device["ip"]?.ToString(), moves) is { } ip) device["ip"] = ip;
        var byMac = state.Get("device")
            .Where(d => d["mac"] != null)
            .GroupBy(d => d["mac"]!.ToString().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var device in live)
        {
            var mac = device["mac"]?.ToString().ToLowerInvariant();
            if (mac == null || !byMac.TryGetValue(mac, out var config)) continue;
            foreach (var (key, value) in config)
            {
                if (key is "_id" or "mac" or "model" or "type" or "version" or "adopted" or ConfigCollections.DeviceMetaField) continue;
                if (key == "radio_table" && value is JsonArray radios && device["radio_table"] is JsonArray liveRadios)
                {
                    foreach (var r in radios.OfType<JsonObject>())
                    {
                        var match = liveRadios.OfType<JsonObject>().FirstOrDefault(l => l["radio"]?.ToString() == r["radio"]?.ToString());
                        if (match == null) continue;
                        foreach (var (rk, rv) in r) match[rk] = rv?.DeepClone();
                    }
                    continue;
                }
                device[key] = value?.DeepClone();
            }
            if (config["port_overrides"] is JsonArray overrides && device["port_table"] is JsonArray ports)
                OverlayPorts(overrides, ports);
        }
        if (state.Collections.ContainsKey("networkconf"))
            OverlayNetworkTables(state, live);
        return new JsonObject { ["meta"] = new JsonObject { ["rc"] = "ok" }, ["data"] = Array(live) }.ToJsonString();
    }

    /// <summary>
    /// The audit reads its networks from the gateway's network_table, which shows them as configured now.
    /// Each entry takes the state's networkconf fields, or a re-addressed plan is audited against the old subnets and DNS.
    /// </summary>
    private static void OverlayNetworkTables(ConfigState state, IEnumerable<JsonObject> live)
    {
        var byId = state.Get("networkconf")
            .Where(n => n["_id"] != null)
            .GroupBy(n => n["_id"]!.ToString())
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var device in live)
        {
            if (device["network_table"] is not JsonArray table) continue;
            foreach (var entry in table.OfType<JsonObject>())
            {
                if (entry["_id"]?.ToString() is not { } id || !byId.TryGetValue(id, out var config)) continue;
                foreach (var (key, value) in config)
                    if (key != ConfigCollections.DeviceMetaField) entry[key] = value?.DeepClone();
            }
        }
    }

    // Port settings the port rules read. An override that leaves one out takes the port profile's or the
    // default, so the live value is dropped; the copy-only ones carry device capability when absent.
    private static readonly string[] PortConfigFields =
    [
        "forward", "tagged_vlan_mgmt", "portconf_id", "native_networkconf_id", "excluded_networkconf_ids",
        "port_security_enabled", "port_security_mac_address", "isolation", "enable",
    ];
    private static readonly string[] PortCopyOnlyFields = ["name", "poe_mode", "op_mode"];

    /// <summary>
    /// The port rules read port_table, which shows the port as configured now. Each port the state overrides
    /// takes the state's settings, or a before/after audit sees the live port on both sides.
    /// </summary>
    private static void OverlayPorts(JsonArray overrides, JsonArray ports)
    {
        var byIdx = overrides.OfType<JsonObject>()
            .Where(o => o["port_idx"] is JsonValue)
            .GroupBy(o => o["port_idx"]!.ToString())
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var port in ports.OfType<JsonObject>())
        {
            if (port["port_idx"]?.ToString() is not { } idx || !byIdx.TryGetValue(idx, out var o)) continue;
            foreach (var field in PortConfigFields)
            {
                if (o[field] is { } v) port[field] = v.DeepClone();
                else port.Remove(field);
            }
            foreach (var field in PortCopyOnlyFields)
                if (o[field] is { } v) port[field] = v.DeepClone();
        }
    }

    /// <summary>Issue identity across two runs: title and what it is about.</summary>
    public static string IssueKey(AuditIssue issue) =>
        $"{issue.Title}|{issue.DeviceMac ?? issue.DeviceName}|{issue.Port}|{issue.CurrentNetwork}|{issue.ClientMac}";

    /// <summary>Regressions (issues added) and fixes (issues removed) between two audit results.</summary>
    public static ImpactResult Compare(AuditResult before, AuditResult after)
    {
        // Informational findings are not issues: they are counted and compared nowhere.
        var beforeIssues = before.Issues.Where(i => i.Severity != AuditSeverity.Informational).ToList();
        var afterIssues = after.Issues.Where(i => i.Severity != AuditSeverity.Informational).ToList();
        var result = new ImpactResult { BeforeIssueCount = beforeIssues.Count, AfterIssueCount = afterIssues.Count };
        var beforeKeys = beforeIssues.Select(IssueKey).ToHashSet(StringComparer.Ordinal);
        var afterKeys = afterIssues.Select(IssueKey).ToHashSet(StringComparer.Ordinal);

        foreach (var issue in afterIssues.DistinctBy(IssueKey))
        {
            var line = Line(issue, "Regression");
            result.AfterIssues.Add(line);
            if (beforeKeys.Contains(line.Key)) continue;
            result.Regressions.Add(line);
            if (result.WorstRegression == null || issue.Severity > result.WorstRegression) result.WorstRegression = issue.Severity;
        }
        foreach (var issue in beforeIssues.DistinctBy(IssueKey).Where(i => !afterKeys.Contains(IssueKey(i))))
            result.Fixes.Add(Line(issue, "Fix"));
        return result;
    }

    private static ImpactLine Line(AuditIssue issue, string kind) => new()
    {
        Kind = kind,
        Title = issue.CurrentNetwork is { Length: > 0 } net && !issue.Title.Contains(net, StringComparison.OrdinalIgnoreCase)
            ? $"{issue.Title} ({net})" : issue.Title,
        Detail = issue.Description ?? "",
        Severity = issue.Severity.ToString(),
        Key = IssueKey(issue),
        Type = issue.Type,
        Network = issue.CurrentNetwork,
        Device = issue.DeviceName,
    };
}
