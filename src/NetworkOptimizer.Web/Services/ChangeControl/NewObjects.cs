using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// The Edit tab's Add: a new object as YAML, ready to fill in, and the check that a new object has
/// what a create needs. Defaults are UniFi's own: the routes the Network web app reads for its create
/// forms. A collection without one, a failed read, or a template uses the static skeleton.
/// </summary>
[VendorSpecific("UniFi", "v2 create-defaults routes the Network web app reads: wlan, lan, port-profiles, firewall-policies")]
public static partial class NewObjects
{
    /// <summary>A required field left for the user: written as "key: " so the editor can put the cursor there.</summary>
    private const string Blank = "__no_blank__";

    /// <summary>
    /// <paramref name="Skeleton"/> fills what UniFi's defaults leave out; <paramref name="Force"/> always wins over them.
    /// <paramref name="Required"/> lists (field path, refused): refused when UniFi Network rejects a create without it.
    /// </summary>
    private sealed record Spec(string Label, string? DefaultsPath, Func<ConfigState, JsonObject> Skeleton,
        Func<JsonObject, IEnumerable<(string Path, bool Refused)>> Required, JsonObject? Force = null);

    // Required fields grounded by creates on a scratch site (Network 11.0.97, 2026-10-29): Refused is a
    // 400/500 from UniFi; the rest are accepted and leave an object that does nothing (a WLAN with no
    // passphrase, a network with no subnet or DHCP range). Policies and zones need a gateway site; their
    // zones and name follow the web app's form.
    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wlanconf"] = new("WLAN", "wlan/defaults", s => new JsonObject
        {
            ["name"] = Blank,
            ["enabled"] = true,
            ["security"] = "wpapsk",
            ["wpa_mode"] = "wpa2",
            ["wpa_enc"] = "ccmp",
            ["x_passphrase"] = Blank,
            ["networkconf_id"] = DefaultLan(s) ?? Blank,
            ["usergroup_id"] = DefaultOf(s, "usergroup"),
            ["ap_group_ids"] = DefaultOf(s, "apgroups") is { } group ? new JsonArray(group) : null,
        }, o => new[] { ("name", true), ("ap_group_ids", true), ("networkconf_id", false) }
            // UniFi's own WPA3 and 6 GHz defaults stay: a create cannot set them, so the plan sets them in
            // an update right after it (PlanBuilder.Wpa3AfterCreate).
            .Concat(Str(o, "security") == "wpapsk" && !Bool(o, "private_preshared_keys_enabled") ? [("x_passphrase", false)] : [])),
        ["networkconf"] = new("Network", "lan/defaults", _ => new JsonObject
        {
            ["name"] = Blank,
            ["enabled"] = true,
            ["purpose"] = "corporate",
            ["vlan_enabled"] = true,
            ["vlan"] = Blank,
            ["ip_subnet"] = Blank,
            ["dhcpd_enabled"] = true,
            ["dhcpd_start"] = Blank,
            ["dhcpd_stop"] = Blank,
        }, o => new[] { ("name", true) }
            // VLAN on without an ID is VLAN 1 to UniFi, which the default LAN holds (api.err.VlanUsed).
            .Concat(Bool(o, "vlan_enabled") ? [("vlan", true)] : [])
            .Concat(Str(o, "purpose") is "corporate" or "guest" ? [("ip_subnet", false)] : [])
            .Concat(Bool(o, "dhcpd_enabled") ? [("dhcpd_start", false), ("dhcpd_stop", false)] : [])),
        ["portconf"] = new("Port Profile", "port-profiles/defaults", s => new JsonObject
        {
            ["name"] = Blank,
            ["native_networkconf_id"] = DefaultLan(s),
        }, _ => [("name", true)]),
        ["firewall_policy"] = new("Firewall Policy", "firewall-policies/defaults", _ => new JsonObject
        {
            ["name"] = Blank,
            ["enabled"] = true,
            ["action"] = "BLOCK",
            ["protocol"] = "all",
            ["ip_version"] = "BOTH",
            ["source"] = new JsonObject { ["zone_id"] = Blank, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY" },
            ["destination"] = new JsonObject { ["zone_id"] = Blank, ["matching_target"] = "ANY", ["port_matching_type"] = "ANY" },
            ["schedule"] = new JsonObject { ["mode"] = "ALWAYS" },
        }, _ => [("name", true), ("source.zone_id", true), ("destination.zone_id", true)],
            // UniFi's defaults pair BLOCK with create_allow_respond true, which a create refuses
            // (api.err.FirewallPolicyCreateRespondTrafficPolicyNotAllowed, main, 2026-10-29).
            Force: new JsonObject { ["create_allow_respond"] = false }),
        // POST firewall/zone {name, network_ids} (grounding.md, 2026-10-23).
        ["firewall_zone"] = new("Firewall Zone", null, _ => new JsonObject { ["name"] = Blank, ["network_ids"] = new JsonArray() },
            _ => [("name", true)]),
        ["firewallgroup"] = new("Firewall Group", null, _ => new JsonObject
        {
            ["name"] = Blank,
            ["group_type"] = "address-group",
            ["group_members"] = new JsonArray(),
        }, _ => [("name", true), ("group_type", true)]),
        ["usergroup"] = new("User Group", null, _ => new JsonObject
        {
            ["name"] = Blank,
            ["qos_rate_max_down"] = -1,
            ["qos_rate_max_up"] = -1,
        }, _ => [("name", false)]),
        ["network_members_group"] = new("Client Group", null, _ => new JsonObject
        {
            ["name"] = Blank,
            ["type"] = "CLIENTS",
            ["members"] = new JsonArray(),
        }, _ => [("name", true), ("type", true)]),
        ["static_dns"] = new("Local DNS Record", null, _ => new JsonObject
        {
            ["key"] = Blank,
            ["enabled"] = true,
            ["record_type"] = "A",
            ["value"] = Blank,
        }, _ => [("key", true), ("record_type", true), ("value", true)]),
    };

    // Defaults UniFi rewrites on create (scratch site, 2026-10-29): ap_group_mode follows the AP groups
    // (All APs reads back "all"); optimize_iot_wifi_connectivity is dropped on a site without the APs for it;
    // the LAN defaults' IPv6 prefix-delegation interface is dropped and the RA lifetime comes back a number.
    // Left out, UniFi sets each itself.
    private static readonly HashSet<string> Omitted = new(StringComparer.Ordinal)
    {
        "wlanconf.ap_group_mode", "wlanconf.optimize_iot_wifi_connectivity",
        "networkconf.ipv6_pd_interface", "networkconf.ipv6_ra_preferred_lifetime",
    };

    /// <summary>Whether the Edit tab offers Add for a collection: a proven create, and never clients or devices.</summary>
    public static bool CanAdd(ConfigCollectionDef def) =>
        Specs.ContainsKey(def.Key) && def.Write is { CreatePath: not null, Proven: true };

    /// <summary>The Add button's noun ("WLAN", "Local DNS Record").</summary>
    public static string Label(ConfigCollectionDef def) => Specs.TryGetValue(def.Key, out var spec) ? spec.Label : def.Noun;

    /// <summary>The v2 site route serving UniFi's create defaults for the collection, or null.</summary>
    public static string? DefaultsPath(ConfigCollectionDef def) => Specs.GetValueOrDefault(def.Key)?.DefaultsPath;

    /// <summary>UniFi's create defaults from the Console, or null when there is no route, no connection, or the read fails.</summary>
    public static async Task<JsonObject?> ReadDefaultsAsync(IUniFiConfigApi? api, ConfigCollectionDef def, CancellationToken ct)
    {
        if (api is not { IsConnected: true } || DefaultsPath(def) is not { } path) return null;
        try
        {
            var response = await api.SendAsync(NetworkOptimizer.UniFi.UniFiApiScope.SiteV2, HttpMethod.Get, path, null, ct);
            return response.IsSuccess ? ParseDefaults(response.Body) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The defaults in a response body: the object, or the first of a list (wlan/defaults: [0] standard, [1] IoT).</summary>
    public static JsonObject? ParseDefaults(string body)
    {
        try
        {
            return JsonNode.Parse(body) switch
            {
                JsonArray a => a.FirstOrDefault() as JsonObject,
                JsonObject o => o,
                _ => null,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One new object as a YAML list item (no collection key, no ID), with references written as names
    /// from <paramref name="baseState"/>. Required fields are blank; secrets are blank, never copied.
    /// </summary>
    public static string Yaml(ConfigCollectionDef def, ConfigState baseState, JsonObject? defaults)
    {
        var spec = Specs[def.Key];
        var obj = new JsonObject();
        foreach (var (key, value) in defaults ?? [])
        {
            // A null default sent back is refused (port profile poe_mode: api.err.InvalidPayload). Empty ones UniFi
            // drops, and a few it rewrites (Omitted); sent, each would read back as not applied after the create.
            if (value is null or JsonArray { Count: 0 } || value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0) continue;
            if (Omitted.Contains($"{def.Key}.{key}")) continue;
            if (def.IsReadOnly(null, key) || key is "_id" or "id" or "predefined" || ConfigCollections.IsSecretField(def, key)) continue;
            if (def.Write?.Allowlist is { } allow && !allow.Contains(key)) continue;
            obj[key] = value?.DeepClone();
        }
        // The skeleton's identity, references, and blanks win; its defaults only fill what UniFi's left out.
        foreach (var (key, value) in spec.Skeleton(baseState))
        {
            if (value == null) continue;
            if (defaults != null && obj.ContainsKey(key) && !IsBlankOrReference(key, value) && value is not JsonObject) continue;
            obj[key] = value is JsonObject nested && obj[key] is JsonObject have ? Merge(have, nested) : value.DeepClone();
        }
        foreach (var (key, value) in spec.Force ?? []) obj[key] = value?.DeepClone();
        obj["_id"] = YamlConverter.TempIdPrefix + "add";

        var state = baseState.Clone();
        state.Collections[def.Key] = [obj];
        var text = YamlConverter.Export(state, [def.Key], SecretMode.Clear);
        // Skip the collection key and any heading (firewall policies' zone pair, of blank zones here):
        // the editor inserts a list item, and a margin comment before "- " broke the add.
        var lines = text.Replace("\r\n", "\n").Split('\n').Skip(1).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => BlankLine().Replace(l, m => $"{m.Groups[1].Value}{YamlConverter.StrippedName(m.Groups[2].Value) ?? m.Groups[2].Value}: "));
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// The required fields a new object leaves empty, as UniFi field paths ("source.zone_id"); Refused
    /// when UniFi Network rejects the create without it, else the object is created and does nothing.
    /// </summary>
    public static IEnumerable<(string Path, bool Refused)> Missing(ConfigCollectionDef def, JsonObject obj)
    {
        if (!Specs.TryGetValue(def.Key, out var spec)) yield break;
        foreach (var (path, refused) in spec.Required(obj))
        {
            JsonNode? node = obj;
            foreach (var part in path.Split('.')) node = (node as JsonObject)?[part];
            if (node is null || node is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)
                || node is JsonArray { Count: 0 })
                yield return (path, refused);
        }
    }

    /// <summary>Field combinations UniFi refuses on a create, as (field path, message).</summary>
    public static IEnumerable<(string Path, string Message)> Refused(ConfigCollectionDef def, JsonObject obj)
    {
        if (def.Key == "firewall_policy" && Bool(obj, "create_allow_respond") && Str(obj, "action") is { } action && action != "ALLOW")
            yield return ("create_allow_respond", $"A new {def.Noun} creates return traffic (create_allow_respond) only with action ALLOW.");
    }

    // Nested defaults (a policy's source) keep UniFi's fields and take the skeleton's blanks.
    private static JsonObject Merge(JsonObject have, JsonObject skeleton)
    {
        var merged = (JsonObject)have.DeepClone();
        foreach (var (key, value) in skeleton)
            if (value != null && (!merged.ContainsKey(key) || IsBlankOrReference(key, value))) merged[key] = value.DeepClone();
        return merged;
    }

    private static bool IsBlankOrReference(string key, JsonNode value) =>
        key is "name" or "key" || YamlConverter.StrippedName(key) != null || value is JsonValue v && v.TryGetValue<string>(out var s) && s == Blank;

    [GeneratedRegex(@"^(\s*(?:- )?)([A-Za-z0-9_.-]+): " + Blank + "$")]
    private static partial Regex BlankLine();

    // The site's untagged default LAN: the network UniFi itself puts new WLANs and ports on.
    private static string? DefaultLan(ConfigState s) =>
        s.Get("networkconf").FirstOrDefault(o => ConfigCollections.NetworkKey(o) == "vlan:1") is { } lan ? ConfigState.IdOf(lan) : null;

    // UniFi's built-in object (attr_hidden_id): the Default user group, the All APs group.
    private static string? DefaultOf(ConfigState s, string collection) =>
        s.Get(collection).FirstOrDefault(o => Str(o, "attr_hidden_id") is { Length: > 0 }) is { } d ? ConfigState.IdOf(d) : null;

    private static string? Str(JsonObject o, string field) => o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonObject o, string field) => o[field] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
