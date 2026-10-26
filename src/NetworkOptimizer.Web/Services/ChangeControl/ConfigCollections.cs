using System.Text.Json.Nodes;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>How a collection's read response is shaped.</summary>
public enum CollectionShape
{
    /// <summary>A list of objects (v1 <c>data</c> array or a v2 array).</summary>
    List,
    /// <summary>One object (v2 dictionaries such as <c>wan/defaults</c>); stored as a single object.</summary>
    Single,
}

/// <summary>Service tags (diff-engine.md). The first four are class D: they alert when unattributed.</summary>
public static class ServiceTags
{
    public const string Mdns = "mDNS/multicast";
    public const string WlanSecurity = "WLAN security";
    public const string DataRates = "Data rates";
    public const string DnsPolicy = "DNS policy";
    public const string Dhcp = "DHCP";
    public const string Ips = "IPS";
    public const string Mlo = "MLO";
    public const string Bands = "Bands";
    public const string Radios = "Radios";
    public const string Poe = "PoE";

    /// <summary>Tags that make an unattributed change alert (class D).</summary>
    public static readonly IReadOnlySet<string> ClassD = new HashSet<string> { Mdns, WlanSecurity, DataRates, DnsPolicy };
}

/// <summary>How Change Control writes one collection. Null on a collection means it is never written.</summary>
public sealed class CollectionWrite
{
    /// <summary>Path to POST a new object to, or null when objects cannot be created.</summary>
    public string? CreatePath { get; init; }

    /// <summary>Path template to update one object; <c>{id}</c> and <c>{key}</c> are replaced.</summary>
    public string? UpdatePath { get; init; }

    /// <summary>HTTP method for updates.</summary>
    public HttpMethod UpdateMethod { get; init; } = HttpMethod.Put;

    /// <summary>Path template to DELETE one object, or null.</summary>
    public string? DeletePath { get; init; }

    /// <summary>POST path taking an array of ids to delete, preferred over per-object deletes when set.</summary>
    public string? BatchDeletePath { get; init; }

    /// <summary>
    /// Fields the endpoint accepts (from the installed app's request DTOs). Null: send the stored
    /// object minus <see cref="ConfigCollectionDef.ReadOnlyFields"/>. v2 endpoints need a list, since
    /// the strict ones reject unknown fields.
    /// </summary>
    public IReadOnlySet<string>? Allowlist { get; init; }

    /// <summary>The endpoint answers an unknown field with a 400 (zones, NAT, static DNS).</summary>
    public bool Strict { get; init; }

    /// <summary>Updates send only changed top-level fields (arrays whole). Devices use this.</summary>
    public bool PartialUpdate { get; init; }

    /// <summary>The write needs a gateway on the site (refused on a gateway-less site).</summary>
    public bool NeedsGateway { get; init; }

    /// <summary>Proven by a write test (grounding.md). Unproven writes still run, and verify reports them.</summary>
    public bool Proven { get; init; }
}

/// <summary>A run of related fields in YAML order, printed under <c># Heading</c>.</summary>
public sealed record FieldGroup(string Heading, IReadOnlyList<string> Fields);

/// <summary>
/// One UniFi Network config collection: where it is read, how its objects are identified,
/// compared, tagged, and written. Grounded in research/config-backup api-findings.md,
/// diff-engine.md, and network-app-10.6-dtos.md.
/// </summary>
public sealed class ConfigCollectionDef
{
    /// <summary>Stable key used in storage and YAML file names.</summary>
    public required string Key { get; init; }

    /// <summary>Name shown in the UI.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Singular noun for summaries ("Port profile Trunk-Core").</summary>
    public required string Noun { get; init; }

    /// <summary>The Edit tab group this collection is listed under.</summary>
    public required string Group { get; init; }

    public required UniFiApiScope Scope { get; init; }
    public required string ReadPath { get; init; }
    public CollectionShape Shape { get; init; } = CollectionShape.List;

    /// <summary>Field holding the object's display name.</summary>
    public string NameField { get; init; } = "name";

    /// <summary>Fields that are read-only or change on their own: stored, never compared, never written.</summary>
    public IReadOnlySet<string> ReadOnlyFields { get; init; } = Empty;

    /// <summary>Array fields whose order does not matter.</summary>
    public IReadOnlySet<string> SetArrays { get; init; } = Empty;

    /// <summary>Array fields of objects compared element by element on a key field (port_overrides by port_idx).</summary>
    public IReadOnlyDictionary<string, string> KeyedArrays { get; init; } = new Dictionary<string, string>();

    /// <summary>Secret fields beyond the <c>x_</c> convention.</summary>
    public IReadOnlySet<string> ExtraSecretFields { get; init; } = Empty;

    /// <summary>
    /// YAML field order for an object, as groups (UniFi field names, before reference stripping).
    /// The first group prints bare; later groups print a <c># Heading</c> line. Fields not listed
    /// follow alphabetically under <c># Other</c>, so a new UniFi field is never dropped. Empty keeps
    /// the name field first and the rest alphabetical.
    /// </summary>
    public IReadOnlyList<FieldGroup> FieldGroups { get; init; } = [];

    /// <summary>Key order inside nested objects (source, destination, schedule); the rest follow alphabetically.</summary>
    public IReadOnlyList<string> NestedFieldOrder { get; init; } = [];

    /// <summary>
    /// Field order per object, keyed by the object's <see cref="NameField"/> value, for a collection
    /// whose objects differ in shape (Settings: one per section). Used when <see cref="FieldGroups"/> is empty.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? SectionFieldOrder { get; init; }

    /// <summary>
    /// Top-level fields kept in the snapshot. Null keeps everything (unknown fields are kept).
    /// Set for stat/device and rest/user, which mix config into telemetry.
    /// </summary>
    public IReadOnlySet<string>? KeepFields { get; init; }

    /// <summary>Restore order (diff-engine.md). Same number = independent. Deletes run in reverse.</summary>
    public int Order { get; init; }

    /// <summary>How the collection is written, or null when Change Control never writes it.</summary>
    public CollectionWrite? Write { get; init; }

    /// <summary>AUDIT log SECTION values that refer to this collection.</summary>
    public IReadOnlyList<string> AuditSections { get; init; } = [];

    /// <summary>AUDIT SETTING_NAME collection values that refer to this collection.</summary>
    public IReadOnlyList<string> AuditCollectionNames { get; init; } = [];

    /// <summary>Changes are recorded but never alerted and are labeled likely UniFi (channel plans).</summary>
    public bool Churn { get; init; }

    /// <summary>Enforced identity key beyond the UniFi ID, or null to fall back to the name.</summary>
    public Func<JsonObject, ConfigIdentityContext, string?>? EnforcedKey { get; init; }

    /// <summary>Whether UniFi enforces <see cref="EnforcedKey"/> as unique (VLAN ID, group name, DNS record, MAC).</summary>
    public bool KeyIsUnique { get; init; }

    /// <summary>Objects UniFi generates and owns (predefined, derived): never written, compared for display only.</summary>
    public Func<JsonObject, bool>? IsGenerated { get; init; }

    /// <summary>
    /// A field UniFi manages on this object (a locked zone's networks): left out of YAML, and a plan
    /// keeps the live value when the file omits it. Snapshots and diffs still carry it.
    /// </summary>
    public Func<JsonObject, string, bool>? HiddenInYaml { get; init; }

    /// <summary>
    /// Values UniFi generates for each site (the device management key, hidden mesh and adoption
    /// SSIDs, built-in secrets). On a plan onto another site, copies the target's own values into the
    /// desired object (first argument) from the live one, and returns what was kept in plain words,
    /// or null when nothing differed.
    /// </summary>
    public Func<JsonObject, JsonObject, string?>? KeepTargetValues { get; init; }

    /// <summary>
    /// A top-level field UniFi manages on some objects only (a PPSK WLAN's passphrase): read-only for
    /// that object, like <see cref="ReadOnlyFields"/> for all of them.
    /// </summary>
    public Func<JsonObject, string, bool>? ManagedField { get; init; }

    /// <summary>
    /// The value UniFi treats a missing top-level field as. Compared that way, so a field UniFi adds on its
    /// own (and no write can remove) is not a difference when it holds the default.
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldDefaults { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether a top-level field is never compared or written, on this object.</summary>
    public bool IsReadOnly(JsonObject? o, string key) =>
        ReadOnlyFields.Contains(key) || (o != null && ManagedField?.Invoke(o, key) == true);

    /// <summary>Service tags for a changed field path on an object of this collection.</summary>
    public Func<JsonObject, string, IEnumerable<string>>? TagsForField { get; init; }

    /// <summary>The object is part of the management path (class B) when this field changes.</summary>
    public Func<JsonObject, string, bool>? IsManagementField { get; init; }

    /// <summary>The object is on the WAN path (WAN networks, routes, port forwards, DNS).</summary>
    public Func<JsonObject, bool>? IsWanPath { get; init; }

    /// <summary>The collection feeds the Security Audit (the impact diff runs when it changes).</summary>
    public bool AuditInput { get; init; }

    internal static readonly IReadOnlySet<string> Empty = new HashSet<string>();
}

/// <summary>Lets identity functions resolve a referenced object's name (zones for policies).</summary>
public sealed class ConfigIdentityContext
{
    private readonly IReadOnlyDictionary<string, string> _namesById;

    public ConfigIdentityContext(IReadOnlyDictionary<string, string> namesById) => _namesById = namesById;

    /// <summary>Display name of the object with this UniFi ID, or the ID itself when unknown.</summary>
    public string NameOf(string? id) =>
        id != null && _namesById.TryGetValue(id, out var name) ? name : id ?? "";
}

/// <summary>The collection catalog.</summary>
[VendorSpecific("UniFi", "Network app 10.6/11 config collections and their write endpoints")]
public static class ConfigCollections
{
    /// <summary>NO-owned object name prefix (Threat Intelligence blocklists and trusted sources).</summary>
    public const string NoOwnedPrefix = "Net. Opt. ";

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.Ordinal);

    private static readonly HashSet<string> CommonReadOnly = Set("site_id", "attr_hidden_id", "attr_no_delete", "attr_no_edit", "external_id");

    private static string? Str(JsonObject o, string field) =>
        o[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o[field]?.ToString();

    private static bool Bool(JsonObject o, string field) =>
        o[field] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static IEnumerable<string> Tags(string field, params (string Pattern, string Tag)[] rules)
    {
        foreach (var (pattern, tag) in rules)
            if (field.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                yield return tag;
    }

    /// <summary>The device config fields kept from stat/device (hardware-replacement.md; sample-device-*.txt).</summary>
    public static readonly IReadOnlySet<string> DeviceConfigFields = Set(
        "_id", "mac", "name", "model", "type", "version", "adopted",
        "port_overrides", "radio_table", "config_network", "config_network_lan", "mgmt_network_id",
        "ethernet_overrides", "led_override", "led_override_color", "led_override_color_brightness",
        "stp_priority", "stp_version", "jumboframe_enabled", "flowctrl_enabled", "dot1x_portctrl_enabled",
        "dot1x_fallback_networkconf_id", "ether_lighting", "link_aggregation_groups", "outdoor_mode_override",
        "mesh_sta_vap_enabled", "mesh_uplink_1", "mesh_uplink_2", "snmp_contact", "snmp_location",
        "switch_vlan_enabled", "lcm_brightness", "lcm_night_mode_begins", "lcm_night_mode_ends",
        "atf_enabled", "bandsteering_mode", "wlangroup_id_na", "wlangroup_id_ng", "wlangroup_id_6e",
        "mbb_overrides", "power_source_ctrl_enabled", "wlan_overrides", "outlet_overrides", "afc_enabled",
        DeviceMetaField);

    /// <summary>Radio config fields; the rest of a radio_table entry is capability (has_*, min/max_txpower, nss).</summary>
    public static readonly IReadOnlySet<string> RadioConfigFields = Set(
        "radio", "name", "channel", "ht", "tx_power_mode", "tx_power", "min_rssi_enabled", "min_rssi",
        "antenna_gain", "antenna_id", "assisted_roaming_enabled", "assisted_roaming_rssi",
        "hard_noise_floor_enabled", "loadbalance_enabled", "sens_level_enabled", "sens_level", "vwire_enabled", "maxsta");

    /// <summary>
    /// Field holding the capability and topology context NO keeps with each device (model caps,
    /// ports, PoE, uplink) for Replace Device. Never compared, never written.
    /// </summary>
    public const string DeviceMetaField = "_no_meta";

    /// <summary>Client config fields kept from rest/user; the rest is telemetry.</summary>
    public static readonly IReadOnlySet<string> ClientConfigFields = Set(
        "_id", "mac", "name", "display_name", "note", "noted", "use_fixedip", "fixed_ip", "network_id",
        "local_dns_record_enabled", "local_dns_record", "virtual_network_override_enabled",
        "virtual_network_override_id", "network_members_group_ids", "usergroup_id", "fixed_ap_enabled",
        "fixed_ap_mac", "blocked", "dev_id_override", "fingerprint_override");

    private static bool IsPredefinedOrDerived(JsonObject o) =>
        Bool(o, "predefined") || Bool(o, "is_predefined")
        || (o["origin_type"] is JsonValue v && v.TryGetValue<string>(out var t) && !string.IsNullOrEmpty(t));

    /// <summary>All collections, in restore order.</summary>
    public static readonly IReadOnlyList<ConfigCollectionDef> All = Build();

    private static readonly Dictionary<string, ConfigCollectionDef> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>A collection by key, or null.</summary>
    public static ConfigCollectionDef? Get(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>True for a secret field: the <c>x_</c> convention plus the collection's extras.</summary>
    public static bool IsSecretField(ConfigCollectionDef? def, string field) =>
        field.StartsWith("x_", StringComparison.Ordinal)
        || (def != null && def.ExtraSecretFields.Contains(field));

    /// <summary>The owning NO feature of an object, or null when the user owns it.</summary>
    public static string? OwnedBy(ConfigCollectionDef def, JsonObject obj)
    {
        var name = Str(obj, def.NameField) ?? Str(obj, "name");
        if (name != null && name.StartsWith(NoOwnedPrefix, StringComparison.Ordinal))
            return "Threat Intelligence";
        return null;
    }

    /// <summary>Copies <paramref name="fields"/> from the target's object into the desired one; <paramref name="label"/> when any differed.</summary>
    private static string? KeepFields(JsonObject desired, JsonObject live, string label, params string[] fields)
    {
        var kept = false;
        foreach (var f in fields)
        {
            if (JsonNode.DeepEquals(desired[f], live[f])) continue;
            if (live[f] is { } value) desired[f] = value.DeepClone();
            else desired.Remove(f);
            kept = true;
        }
        return kept ? label : null;
    }

    /// <summary>Keeps the target's x_secret on each RADIUS server it also has (same IP and port).</summary>
    private static bool KeepServerSecrets(JsonObject desired, JsonObject live)
    {
        var kept = false;
        foreach (var list in new[] { "auth_servers", "acct_servers" })
        {
            if (desired[list] is not JsonArray want || live[list] is not JsonArray have) continue;
            foreach (var server in want.OfType<JsonObject>())
            {
                var match = have.OfType<JsonObject>().FirstOrDefault(h =>
                    Str(h, "ip") == Str(server, "ip") && Str(h, "port") == Str(server, "port"));
                if (match?["x_secret"] is not { } secret || JsonNode.DeepEquals(secret, server["x_secret"])) continue;
                server["x_secret"] = secret.DeepClone();
                kept = true;
            }
        }
        return kept;
    }

    /// <summary>Fields of a NO-owned object that are its generated contents, not its shell.</summary>
    public static readonly IReadOnlySet<string> NoOwnedContentFields = Set("group_members", "ips", "ip_addresses");

    private static List<ConfigCollectionDef> Build()
    {
        var policyAllow = Set("_id", "enabled", "predefined", "name", "description", "action", "index", "source", "destination",
            "ip_version", "protocol", "icmp_typename", "icmp_v6_typename", "match_opposite_protocol", "connection_state_type",
            "connection_states", "match_ip_sec", "match_ip_sec_type", "logging", "create_allow_respond", "schedule");

        return
        [
            new()
            {
                Key = "setting", DisplayName = "Settings", Noun = "Setting", Group = "Settings",
                SectionFieldOrder = ConfigFieldOrder.SettingSections,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/setting", NameField = "key", Order = 1,
                // hostname (super_identity) is the console's OS hostname, which UniFi fills in; a container recreate changes it.
                // network_defaults (ether_lighting) are the colors UniFi assigns each network; a write of them is ignored.
                // x_api_token (mgmt) is issued anew by UniFi on every write of mgmt.
                ReadOnlyFields = Set("site_id", "last_alert_id", "available_controller_channels", "available_firmware_channels", "layout_preference", "widgets", "hostname",
                    "network_defaults", "x_api_token"),
                ExtraSecretFields = Set("psk", "utm_token", "fingerbank_key"),
                SetArrays = Set("enabled_networks", "excluded_network_ids", "enabled_categories", "geo_ip_filtering_countries"),
                EnforcedKey = (o, _) => Str(o, "key"), KeyIsUnique = true,
                Write = new CollectionWrite { UpdatePath = "rest/setting/{key}/{id}", Proven = true },
                AuditSections = ["SETTINGS", "SYSTEM", "NETWORK_GLOBAL", "INTERNET", "WIFI_GLOBAL"],
                AuditCollectionNames = ["setting"], AuditInput = true,
                TagsForField = (o, f) => (Str(o, "key") ?? "") switch
                {
                    "mdns" => [ServiceTags.Mdns],
                    "ips" => f.Contains("dns", StringComparison.OrdinalIgnoreCase) ? [ServiceTags.DnsPolicy, ServiceTags.Ips] : [ServiceTags.Ips],
                    "doh" => [ServiceTags.DnsPolicy],
                    "global_switch" => Tags(f, ("mcast", ServiceTags.Mdns), ("multicast", ServiceTags.Mdns), ("igmp", ServiceTags.Mdns), ("dhcp", ServiceTags.Dhcp)),
                    _ => Tags(f, ("mdns", ServiceTags.Mdns), ("igmp", ServiceTags.Mdns)),
                },
                IsManagementField = (o, f) => (Str(o, "key"), f) is ("super_mgmt", "override_inform_host") or ("super_mgmt", "override_inform_host_location"),
                // Differ between two new sites on one console with no user action (grounding.md, 2026-10-26).
                // Device SSH credentials (x_ssh_username, x_ssh_password) are user config and do carry.
                KeepTargetValues = (d, l) => (Str(d, "key") ?? "") switch
                {
                    "mgmt" => KeepFields(d, l, "Device management key", "x_mgmt_key"),
                    "connectivity" => KeepFields(d, l, "Mesh SSID and key", "x_mesh_essid", "x_mesh_psk"),
                    "element_adopt" => KeepFields(d, l, "Adoption SSID and key", "x_element_essid", "x_element_psk"),
                    "peer_to_peer" => KeepFields(d, l, "Peer-to-peer SSID and key", "ssid", "psk"),
                    "openvpn" => KeepFields(d, l, "OpenVPN DH key", "x_pregenerated_dh_key"),
                    _ => null,
                },
                IsWanPath = o => Str(o, "key") is "doh" or "global_nat",
            },
            new()
            {
                Key = "networkconf", DisplayName = "Networks", Noun = "Network", Group = "Networks",
                FieldGroups = ConfigFieldOrder.NetworkFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/networkconf", Order = 2,
                ReadOnlyFields = CommonReadOnly,
                // Not x_-prefixed, but carry keys: the WireGuard config file holds the PrivateKey line.
                ExtraSecretFields = Set("wireguard_client_preshared_key", "wireguard_client_configuration_file", "openvpn_configuration"),
                SetArrays = Set("dhcpd_dns", "nat_outbound_ip_addresses"),
                EnforcedKey = (o, _) => NetworkKey(o), KeyIsUnique = true,
                Write = new CollectionWrite
                {
                    CreatePath = "rest/networkconf", UpdatePath = "rest/networkconf/{id}", DeletePath = "rest/networkconf/{id}", Proven = true,
                },
                AuditSections = ["NETWORK_LOCAL", "NETWORK_WAN", "NETWORK_VPN_SERVER", "NETWORK_VPN_CLIENT"],
                AuditCollectionNames = ["networkconf"], AuditInput = true,
                TagsForField = (_, f) => Tags(f, ("mdns", ServiceTags.Mdns), ("igmp", ServiceTags.Mdns), ("multicast", ServiceTags.Mdns),
                    ("dhcpd_dns", ServiceTags.DnsPolicy), ("dhcp", ServiceTags.Dhcp)),
                IsManagementField = (o, f) => f is "ip_subnet" or "vlan" or "vlan_enabled" or "dhcpd_enabled" or "purpose" or "networkgroup",
                IsWanPath = o => Str(o, "purpose") is "wan",
            },
            new()
            {
                Key = "usergroup", DisplayName = "User groups", Noun = "User group", Group = "QoS and Traffic Rules",
                FieldGroups = ConfigFieldOrder.UserGroupFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/usergroup", Order = 2, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/usergroup", UpdatePath = "rest/usergroup/{id}", DeletePath = "rest/usergroup/{id}", Proven = true },
                AuditCollectionNames = ["usergroup"],
            },
            new()
            {
                Key = "radiusprofile", DisplayName = "RADIUS profiles", Noun = "RADIUS profile", Group = "RADIUS and VPN",
                FieldGroups = ConfigFieldOrder.RadiusProfileFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/radiusprofile", Order = 2, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/radiusprofile", UpdatePath = "rest/radiusprofile/{id}", DeletePath = "rest/radiusprofile/{id}" },
                AuditCollectionNames = ["radiusprofile"],
                // The built-in profile's server secret is random per site.
                KeepTargetValues = (d, l) => DeleteCascade.IsProtected(l) && KeepServerSecrets(d, l)
                    ? $"Server secret on RADIUS profile {Str(l, "name")}" : null,
                TagsForField = (_, _) => [ServiceTags.WlanSecurity],
            },
            new()
            {
                Key = "firewall_zone", DisplayName = "Firewall zones", Noun = "Firewall zone", Group = "Firewall Zones",
                FieldGroups = ConfigFieldOrder.FirewallZoneFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "firewall/zone", Order = 3,
                ReadOnlyFields = Set("attr_no_edit", "cloud_template", "default_zone", "external_id", "zone_key"),
                SetArrays = Set("network_ids"),
                // External, Gateway, and VPN are locked (attr_no_edit); UniFi fills their networks (WANs into External).
                HiddenInYaml = (o, field) => field == "network_ids" && Bool(o, "attr_no_edit"),
                EnforcedKey = (o, _) => Str(o, "zone_key") is { Length: > 0 } k ? "zone_key:" + k : null,
                Write = new CollectionWrite
                {
                    CreatePath = "firewall/zone", UpdatePath = "firewall/zone/{id}", DeletePath = "firewall/zone/{id}",
                    BatchDeletePath = "firewall/zone/batch-delete", Allowlist = Set("_id", "name", "network_ids"), Strict = true,
                    NeedsGateway = true, Proven = true,
                },
                AuditSections = ["FIREWALL_ZONE"], AuditCollectionNames = ["firewall_zone"], AuditInput = true,
            },
            new()
            {
                Key = "firewallgroup", DisplayName = "Firewall groups", Noun = "Firewall group", Group = "Firewall Groups",
                FieldGroups = ConfigFieldOrder.FirewallGroupFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/firewallgroup", Order = 3, ReadOnlyFields = CommonReadOnly,
                // A group with no source is static (UniFi's UI writes "static" on its first save); a write
                // cannot remove the field, so missing and "static" must compare equal.
                FieldDefaults = new Dictionary<string, string> { ["source"] = "static" },
                SetArrays = Set("group_members"),
                EnforcedKey = (o, _) => Str(o, "name"), KeyIsUnique = true,
                Write = new CollectionWrite { CreatePath = "rest/firewallgroup", UpdatePath = "rest/firewallgroup/{id}", DeletePath = "rest/firewallgroup/{id}", Proven = true },
                AuditSections = ["FIREWALL_GROUP"], AuditCollectionNames = ["firewallgroup"], AuditInput = true,
            },
            new()
            {
                Key = "portconf", DisplayName = "Port profiles", Noun = "Port profile", Group = "Port Profiles",
                FieldGroups = ConfigFieldOrder.PortProfileFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/portconf", Order = 4, ReadOnlyFields = CommonReadOnly,
                SetArrays = Set("excluded_networkconf_ids", "multicast_router_networkconf_ids", "tagged_networkconf_ids"),
                Write = new CollectionWrite { CreatePath = "rest/portconf", UpdatePath = "rest/portconf/{id}", DeletePath = "rest/portconf/{id}", Proven = true },
                AuditSections = ["PORT_PROFILE"], AuditCollectionNames = ["portconf"], AuditInput = true,
                TagsForField = (_, f) => Tags(f, ("multicast", ServiceTags.Mdns), ("poe", ServiceTags.Poe)),
                IsManagementField = (_, f) => f is "native_networkconf_id" or "tagged_vlan_mgmt" or "excluded_networkconf_ids" or "forward" or "tagged_networkconf_ids",
            },
            new()
            {
                Key = "apgroups", DisplayName = "AP groups", Noun = "AP group", Group = "WLANs",
                FieldGroups = ConfigFieldOrder.ApGroupFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "apgroups", Order = 4, ReadOnlyFields = CommonReadOnly,
                SetArrays = Set("device_macs"),
                Write = new CollectionWrite
                {
                    CreatePath = "apgroups", UpdatePath = "apgroups/{id}", DeletePath = "apgroups/{id}",
                    Allowlist = Set("_id", "name", "device_macs", "for_wlanconf"),
                },
                AuditCollectionNames = ["apgroup"],
            },
            new()
            {
                Key = "wlangroup", DisplayName = "WLAN groups", Noun = "WLAN group", Group = "WLANs",
                FieldGroups = ConfigFieldOrder.WlanGroupFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/wlangroup", Order = 4, ReadOnlyFields = CommonReadOnly,
            },
            new()
            {
                Key = "wlanconf", DisplayName = "WLANs", Noun = "WLAN", Group = "WLANs",
                FieldGroups = ConfigFieldOrder.WlanFields,
                // wlan_band is the legacy band field UniFi derives from wlan_bands. Sent on a create, it wins:
                // "both" drops 6 GHz (main, 10.6.109). The WLAN's bands are wlan_bands.
                // short_id is an index UniFi assigns per site; a create gets the next free one.
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/wlanconf", Order = 5, ReadOnlyFields = Set([.. CommonReadOnly, "wlan_band", "short_id"]),
                // With private PSKs on, UniFi generates x_passphrase and ignores any write of it; the keys are the PSK entries.
                ManagedField = (o, f) => f == "x_passphrase" && o["private_preshared_keys_enabled"] is JsonValue v && v.TryGetValue<bool>(out var on) && on,
                // Inside private_preshared_keys and sae_psk entries. PlanBuilder.RestoreKeptSecrets keeps them per entry.
                ExtraSecretFields = Set("password", "psk"),
                SetArrays = Set("ap_group_ids", "wlan_bands", "mac_filter_list", "schedule", "schedule_with_duration"),
                EnforcedKey = (o, ctx) => $"{Str(o, "name")}@{ctx.NameOf(Str(o, "networkconf_id"))}",
                Write = new CollectionWrite { CreatePath = "rest/wlanconf", UpdatePath = "rest/wlanconf/{id}", DeletePath = "rest/wlanconf/{id}", Proven = true },
                AuditSections = ["WIFI", "WLAN"], AuditCollectionNames = ["wlanconf"], AuditInput = true,
                TagsForField = (_, f) => Tags(f, ("security", ServiceTags.WlanSecurity), ("wpa", ServiceTags.WlanSecurity), ("pmf", ServiceTags.WlanSecurity),
                    ("passphrase", ServiceTags.WlanSecurity), ("radius", ServiceTags.WlanSecurity), ("minrate", ServiceTags.DataRates),
                    ("data_rate", ServiceTags.DataRates), ("basic_rate", ServiceTags.DataRates), ("mcast", ServiceTags.Mdns),
                    ("multicast", ServiceTags.Mdns), ("bc_filter", ServiceTags.Mdns), ("mlo", ServiceTags.Mlo), ("wlan_band", ServiceTags.Bands)),
            },
            new()
            {
                Key = "firewall_policy", DisplayName = "Firewall policies", Noun = "Firewall policy", Group = "Firewall Policies",
                FieldGroups = ConfigFieldOrder.FirewallPolicyFields, NestedFieldOrder = ConfigFieldOrder.FirewallPolicyNested,
                Scope = UniFiApiScope.SiteV2, ReadPath = "firewall-policies", Order = 6,
                ReadOnlyFields = Set("hits", "last_hit", "index", "origin_id", "origin_type", "cloud_template", "external_id"),
                EnforcedKey = (o, ctx) =>
                    $"{Str(o, "name")}@{ctx.NameOf(Str(o["source"] as JsonObject ?? [], "zone_id"))}>{ctx.NameOf(Str(o["destination"] as JsonObject ?? [], "zone_id"))}",
                IsGenerated = IsPredefinedOrDerived,
                Write = new CollectionWrite
                {
                    CreatePath = "firewall-policies", UpdatePath = "firewall-policies/{id}", BatchDeletePath = "firewall-policies/batch-delete",
                    Allowlist = policyAllow, NeedsGateway = true, Proven = true,
                },
                AuditSections = ["FIREWALL_POLICY"], AuditCollectionNames = ["firewall_policy", "firewallpolicy"], AuditInput = true,
            },
            new()
            {
                Key = "static_dns", DisplayName = "Local DNS", Noun = "Local DNS", Group = "Local DNS",
                FieldGroups = ConfigFieldOrder.LocalDnsFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "static-dns", NameField = "key", Order = 5,
                EnforcedKey = (o, _) => $"{Str(o, "key")}|{Str(o, "record_type")}|{Str(o, "value")}", KeyIsUnique = true,
                Write = new CollectionWrite
                {
                    CreatePath = "static-dns", UpdatePath = "static-dns/{id}", DeletePath = "static-dns/{id}",
                    Allowlist = Set("_id", "enabled", "record_type", "key", "value", "priority", "ttl", "weight", "port"), Strict = true, Proven = true,
                },
                AuditSections = ["STATIC_DNS"], AuditCollectionNames = ["static_dns"], AuditInput = true,
                TagsForField = (_, _) => [ServiceTags.DnsPolicy],
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "routing", DisplayName = "Static routes", Noun = "Static route", Group = "Routes and Traffic Routes",
                FieldGroups = ConfigFieldOrder.StaticRouteFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/routing", Order = 8, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/routing", UpdatePath = "rest/routing/{id}", DeletePath = "rest/routing/{id}", NeedsGateway = true },
                AuditSections = ["ROUTING", "STATIC_ROUTE"], AuditCollectionNames = ["routing"],
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "trafficroute", DisplayName = "Traffic routes", Noun = "Traffic route", Group = "Routes and Traffic Routes",
                FieldGroups = ConfigFieldOrder.TrafficRouteFields, NestedFieldOrder = ConfigFieldOrder.TrafficRouteNested,
                Scope = UniFiApiScope.SiteV2, ReadPath = "trafficroutes", NameField = "description", Order = 8,
                SetArrays = Set("domains", "ip_addresses", "ip_ranges", "regions", "target_devices"),
                Write = new CollectionWrite
                {
                    CreatePath = "trafficroutes", UpdatePath = "trafficroutes/{id}", DeletePath = "trafficroutes/{id}", NeedsGateway = true,
                    Allowlist = Set("_id", "enabled", "description", "matching_target", "domains", "ip_addresses", "ip_ranges", "regions",
                        "target_devices", "network_id", "next_hop", "kill_switch_enabled"),
                },
                AuditSections = ["TRAFFIC_ROUTE"], AuditCollectionNames = ["trafficroute"],
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "qos_rule", DisplayName = "QoS rules", Noun = "QoS rule", Group = "QoS and Traffic Rules",
                FieldGroups = ConfigFieldOrder.QosRuleFields, NestedFieldOrder = ConfigFieldOrder.FirewallPolicyNested,
                Scope = UniFiApiScope.SiteV2, ReadPath = "qos-rules", Order = 8,
                ReadOnlyFields = Set("index", "cloud_template", "origin_id", "origin_type"),
                IsGenerated = IsPredefinedOrDerived,
                Write = new CollectionWrite
                {
                    CreatePath = "qos-rules", UpdatePath = "qos-rules/{id}", DeletePath = "qos-rules/{id}", BatchDeletePath = "qos-rules/batch-delete",
                    NeedsGateway = true,
                    Allowlist = Set("_id", "enabled", "name", "objective", "source", "destination", "wan_or_vpn_network", "download_limit_kbps",
                        "download_burst", "upload_limit_kbps", "upload_burst", "schedule"),
                },
                AuditSections = ["QOS_RULE"], AuditCollectionNames = ["qos_rule"],
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "nat", DisplayName = "NAT", Noun = "NAT rule", Group = "NAT and Port Forwards",
                FieldGroups = ConfigFieldOrder.NatFields, NestedFieldOrder = ConfigFieldOrder.NatNested,
                Scope = UniFiApiScope.SiteV2, ReadPath = "nat", NameField = "description", Order = 8,
                ReadOnlyFields = Set("rule_index"),
                IsGenerated = o => Bool(o, "is_predefined"),
                Write = new CollectionWrite
                {
                    CreatePath = "nat", UpdatePath = "nat/{id}", DeletePath = "nat/{id}", NeedsGateway = true, Strict = true,
                    Allowlist = Set("_id", "enabled", "type", "description", "protocol", "in_interface", "out_interface", "pppoe_use_base_interface",
                        "ip_version", "ip_address", "port", "source_filter", "destination_filter", "logging", "exclude", "setting_preference"),
                },
                AuditSections = ["NAT"], AuditCollectionNames = ["nat"], AuditInput = true,
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "portforward", DisplayName = "Port forwards", Noun = "Port forward", Group = "NAT and Port Forwards",
                FieldGroups = ConfigFieldOrder.PortForwardFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/portforward", Order = 8, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/portforward", UpdatePath = "rest/portforward/{id}", DeletePath = "rest/portforward/{id}", NeedsGateway = true },
                AuditSections = ["PORT_FORWARD"], AuditCollectionNames = ["portforward"], AuditInput = true,
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "oon_config", DisplayName = "Object-oriented configs", Noun = "Object config", Group = "QoS and Traffic Rules",
                FieldGroups = ConfigFieldOrder.ObjectConfigFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "object-oriented-network-configs", Order = 8,
                Write = new CollectionWrite
                {
                    CreatePath = "object-oriented-network-config", UpdatePath = "object-oriented-network-config/{id}",
                    DeletePath = "object-oriented-network-config/{id}",
                    Allowlist = Set("id", "_id", "name", "target_type", "targets", "secure", "route", "qos"),
                },
            },
            new()
            {
                Key = "dynamicdns", DisplayName = "Dynamic DNS", Noun = "Dynamic DNS", Group = "RADIUS and VPN",
                FieldGroups = ConfigFieldOrder.DynamicDnsFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/dynamicdns", NameField = "host_name", Order = 8, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/dynamicdns", UpdatePath = "rest/dynamicdns/{id}", DeletePath = "rest/dynamicdns/{id}", NeedsGateway = true },
                IsWanPath = _ => true,
            },
            new()
            {
                Key = "wireguard_user", DisplayName = "WireGuard VPN users", Noun = "WireGuard user", Group = "RADIUS and VPN",
                FieldGroups = ConfigFieldOrder.WireGuardUserFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "wireguard/users", Order = 8,
                // Network 11 stores and serves these without the x_ prefix (its schema: wireguard_user.pre_shared_key).
                ExtraSecretFields = Set("pre_shared_key", "private_key"),
                AuditSections = ["WIREGUARD_VPN_USER"],
            },
            new()
            {
                Key = "radius_profile_v2", DisplayName = "RADIUS profiles (v2)", Noun = "RADIUS profile", Group = "RADIUS and VPN",
                FieldGroups = ConfigFieldOrder.RadiusProfileFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "radius/profiles", Order = 8,
            },
            new()
            {
                Key = "vpn_connections", DisplayName = "VPN connections", Noun = "VPN connections", Group = "RADIUS and VPN",
                Scope = UniFiApiScope.SiteV2, ReadPath = "vpn/connections", Shape = CollectionShape.Single, Order = 8,
                // Live tunnel state (status, assoc_time, rates, IPs) per networkconf id: a reconnect is not a config
                // change. The VPN's config is its networkconf object, compared there.
                ReadOnlyFields = Set("connections"),
            },
            new()
            {
                Key = "wan_defaults", DisplayName = "WAN defaults", Noun = "WAN defaults", Group = "Networks",
                Scope = UniFiApiScope.SiteV2, ReadPath = "wan/defaults", Shape = CollectionShape.Single, Order = 8,
            },
            new()
            {
                Key = "scheduletask", DisplayName = "Scheduled tasks", Noun = "Scheduled task", Group = "Other",
                FieldGroups = ConfigFieldOrder.ScheduledTaskFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/scheduletask", Order = 8, ReadOnlyFields = CommonReadOnly,
            },
            new()
            {
                Key = "dhcpoption", DisplayName = "DHCP options", Noun = "DHCP option", Group = "Networks",
                FieldGroups = ConfigFieldOrder.DhcpOptionFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/dhcpoption", Order = 2, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/dhcpoption", UpdatePath = "rest/dhcpoption/{id}", DeletePath = "rest/dhcpoption/{id}" },
                TagsForField = (_, _) => [ServiceTags.Dhcp],
            },
            new()
            {
                Key = "account", DisplayName = "RADIUS accounts", Noun = "RADIUS account", Group = "RADIUS and VPN",
                FieldGroups = ConfigFieldOrder.RadiusAccountFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/account", Order = 2, ReadOnlyFields = CommonReadOnly,
                Write = new CollectionWrite { CreatePath = "rest/account", UpdatePath = "rest/account/{id}", DeletePath = "rest/account/{id}" },
            },
            new()
            {
                Key = "hotspotop", DisplayName = "Hotspot operators", Noun = "Hotspot operator", Group = "Other",
                FieldGroups = ConfigFieldOrder.HotspotOperatorFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/hotspotop", Order = 8, ReadOnlyFields = CommonReadOnly,
            },
            new()
            {
                Key = "hotspotpackage", DisplayName = "Hotspot packages", Noun = "Hotspot package", Group = "Other",
                FieldGroups = ConfigFieldOrder.HotspotPackageFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/hotspotpackage", Order = 8, ReadOnlyFields = CommonReadOnly,
            },
            new()
            {
                Key = "trafficrule", DisplayName = "Traffic rules", Noun = "Traffic rule", Group = "QoS and Traffic Rules",
                Scope = UniFiApiScope.SiteV2, ReadPath = "trafficrules", NameField = "description", Order = 8,
            },
            new()
            {
                Key = "content_filtering", DisplayName = "Content filtering", Noun = "Content filter", Group = "Firewall Policies",
                FieldGroups = ConfigFieldOrder.ContentFilteringFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "content-filtering", Order = 8,
                TagsForField = (_, _) => [ServiceTags.DnsPolicy],
            },
            new()
            {
                Key = "acl_rule", DisplayName = "ACL rules", Noun = "ACL rule", Group = "Firewall Policies",
                FieldGroups = ConfigFieldOrder.AclRuleFields,
                Scope = UniFiApiScope.SiteV2, ReadPath = "acl-rules", Order = 8,
            },
            new()
            {
                Key = "firewallrule", DisplayName = "Legacy firewall rules", Noun = "Legacy firewall rule", Group = "Firewall Policies",
                FieldGroups = ConfigFieldOrder.LegacyFirewallRuleFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/firewallrule", Order = 6, ReadOnlyFields = CommonReadOnly,
                AuditInput = true,
            },
            new()
            {
                Key = "channelplan", DisplayName = "Channel plans", Noun = "Channel plan", Group = "Devices",
                FieldGroups = ConfigFieldOrder.ChannelPlanFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/channelplan", Order = 99, Churn = true, ReadOnlyFields = CommonReadOnly,
            },
            new()
            {
                Key = "user", DisplayName = "Clients", Noun = "Client", Group = "Clients",
                FieldGroups = ConfigFieldOrder.ClientFields,
                Scope = UniFiApiScope.SiteV1, ReadPath = "rest/user", Order = 9, KeepFields = ClientConfigFields,
                SetArrays = Set("network_members_group_ids"),
                EnforcedKey = (o, _) => Str(o, "mac")?.ToLowerInvariant(), KeyIsUnique = true,
                Write = new CollectionWrite { CreatePath = "rest/user", UpdatePath = "rest/user/{id}", PartialUpdate = true, Proven = true },
                AuditSections = ["CLIENT"], AuditCollectionNames = ["user"],
                TagsForField = (_, f) => Tags(f, ("local_dns", ServiceTags.DnsPolicy)),
            },
            new()
            {
                Key = "device", DisplayName = "Devices", Noun = "Device", Group = "Devices",
                FieldGroups = ConfigFieldOrder.DeviceFields, NestedFieldOrder = ConfigFieldOrder.DeviceNested,
                Scope = UniFiApiScope.SiteV1, ReadPath = "stat/device", Order = 10, KeepFields = DeviceConfigFields,
                ReadOnlyFields = Set("model", "type", "version", "adopted", DeviceMetaField),
                KeyedArrays = new Dictionary<string, string>
                {
                    ["port_overrides"] = "port_idx", ["radio_table"] = "radio", ["ethernet_overrides"] = "ifname",
                    ["link_aggregation_groups"] = "id",
                },
                EnforcedKey = (o, _) => Str(o, "mac")?.ToLowerInvariant(), KeyIsUnique = true,
                Write = new CollectionWrite { UpdatePath = "rest/device/{id}", PartialUpdate = true, Proven = true },
                AuditSections = ["DEVICE"], AuditCollectionNames = ["device"], AuditInput = true,
                TagsForField = (_, f) => Tags(f, ("radio_table", ServiceTags.Radios), ("poe", ServiceTags.Poe),
                    ("minrate", ServiceTags.DataRates), ("data_rate", ServiceTags.DataRates)),
                IsManagementField = (_, f) =>
                    f.StartsWith("mgmt_network_id", StringComparison.Ordinal) || f.StartsWith("config_network", StringComparison.Ordinal)
                    || f.Contains("native_networkconf_id", StringComparison.Ordinal) || f.Contains("tagged_vlan_mgmt", StringComparison.Ordinal)
                    || f.Contains("excluded_networkconf_ids", StringComparison.Ordinal) || f.Contains("portconf_id", StringComparison.Ordinal)
                    || f.StartsWith("ethernet_overrides", StringComparison.Ordinal),
            },
        ];
    }

    /// <summary>Network identity: VLAN ID (UniFi enforces it), the WAN group for WANs, or the default LAN.</summary>
    internal static string? NetworkKey(JsonObject o)
    {
        var purpose = Str(o, "purpose");
        if (purpose == "wan" && Str(o, "wan_networkgroup") is { Length: > 0 } group)
            return "wan:" + group;
        if (Bool(o, "vlan_enabled") && o["vlan"] is JsonNode vlan && vlan.ToString() is { Length: > 0 } v)
            return "vlan:" + v;
        if (purpose is "corporate" && !Bool(o, "vlan_enabled") && Str(o, "networkgroup") is null or "LAN")
            return "vlan:1";
        return null;
    }
}
