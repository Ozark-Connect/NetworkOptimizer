using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One displayed field row: label, old and new text. Secrets show no values.</summary>
public sealed record FieldRow(string Label, string Old, string New, bool Secret);

/// <summary>
/// Turns diffs into the words the UI shows: one summary line per object ("WLAN Home: security WPA2
/// to WPA2/WPA3"), field labels, and values with references rendered as names.
/// </summary>
public static partial class ChangeDescriber
{
    /// <summary>The synthetic WLAN row that stands for wpa_mode, wpa3_support, wpa3_transition, and wpa_enc.</summary>
    public const string SecurityProtocol = "Security Protocol";

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["enabled"] = "Enabled", ["name"] = "Name", ["action"] = "Action", ["description"] = "Description",
        ["security"] = "Security", ["wpa_mode"] = "Security", ["wpa3_support"] = "Security", ["wpa3_transition"] = "Security",
        ["wpa_enc"] = "Security", ["pmf_mode"] = "PMF", ["x_passphrase"] = "Passphrase", ["x_iapp_key"] = "Fast Roaming Key",
        ["native_networkconf_id"] = "Native Network", ["tagged_networkconf_ids"] = "Tagged Networks",
        ["excluded_networkconf_ids"] = "Tagged Networks", ["tagged_vlan_mgmt"] = "Tagged VLAN Management",
        ["networkconf_id"] = "Network", ["network_id"] = "Network", ["network_ids"] = "Networks",
        ["ip_subnet"] = "Subnet", ["vlan"] = "VLAN", ["vlan_enabled"] = "VLAN", ["mdns_enabled"] = "mDNS",
        ["igmp_snooping"] = "IGMP Snooping", ["dhcpd_enabled"] = "DHCP", ["dhcpd_dns_enabled"] = "DHCP DNS",
        ["wlan_bands"] = "Bands", ["mlo_enabled"] = "MLO", ["hide_ssid"] = "Hidden", ["ap_group_ids"] = "AP Groups",
        ["group_members"] = "Members", ["fixed_ip"] = "Fixed IP", ["use_fixedip"] = "Fixed IP", ["note"] = "Note",
        ["mgmt_network_id"] = "Management Network", ["config_network"] = "IP Settings", ["led_override"] = "LED",
        ["stp_priority"] = "STP Priority", ["poe_mode"] = "PoE", ["op_mode"] = "Port Mode", ["forward"] = "Port Mode",
        ["portconf_id"] = "Port Profile", ["value"] = "Value", ["record_type"] = "Type", ["key"] = "Hostname",
        ["source"] = "Source", ["destination"] = "Destination", ["schedule"] = "Schedule", ["logging"] = "Logging",
        ["protocol"] = "Protocol", ["ip_version"] = "IP Version", ["channel"] = "Channel", ["ht"] = "Channel Width",
        ["tx_power_mode"] = "TX Power", ["tx_power"] = "TX Power", ["min_rssi"] = "Min RSSI", ["min_rssi_enabled"] = "Min RSSI",
        ["dhcpd_dns_1"] = "DNS Server", ["dhcpd_dns_2"] = "DNS Server", ["wan_dns1"] = "DNS Server", ["wan_dns2"] = "DNS Server",
        ["port_security_enabled"] = "Port Security", ["port_security_mac_address"] = "MAC Address Filter",
        ["trusted_port_mac"] = "Lock Port to UniFi Device",
    };

    // UniFi field shorthand and acronyms, as a title shows them. Every other word is capitalized.
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["autoneg"] = "Auto-Negotiation", ["dot1x"] = "802.1X", ["ctrl"] = "Control", ["mgmt"] = "Management",
        ["lldpmed"] = "LLDP-MED", ["lldp"] = "LLDP", ["stp"] = "STP", ["poe"] = "PoE", ["sfp"] = "SFP", ["lacp"] = "LACP",
        ["fec"] = "FEC", ["mtu"] = "MTU", ["led"] = "LED", ["vlan"] = "VLAN", ["igmp"] = "IGMP", ["mdns"] = "mDNS",
        ["ip"] = "IP", ["ipv4"] = "IPv4", ["ipv6"] = "IPv6", ["mac"] = "MAC", ["dhcp"] = "DHCP", ["dhcpd"] = "DHCP",
        ["dns"] = "DNS", ["ntp"] = "NTP", ["snmp"] = "SNMP", ["ssh"] = "SSH", ["http"] = "HTTP", ["https"] = "HTTPS",
        ["tcp"] = "TCP", ["udp"] = "UDP", ["icmp"] = "ICMP", ["nat"] = "NAT", ["upnp"] = "UPnP", ["vpn"] = "VPN",
        ["ipsec"] = "IPsec", ["l2tp"] = "L2TP", ["gre"] = "GRE", ["bgp"] = "BGP", ["ospf"] = "OSPF", ["qos"] = "QoS",
        ["ttl"] = "TTL", ["url"] = "URL", ["cidr"] = "CIDR", ["wan"] = "WAN", ["lan"] = "LAN", ["ssid"] = "SSID",
        ["ap"] = "AP", ["wlan"] = "WLAN", ["rssi"] = "RSSI", ["tx"] = "TX", ["rx"] = "RX", ["pmf"] = "PMF", ["mlo"] = "MLO",
        ["wpa"] = "WPA", ["radius"] = "RADIUS", ["dpi"] = "DPI", ["ips"] = "IPS", ["id"] = "ID", ["wifi"] = "Wi-Fi",
        ["unifi"] = "UniFi",
    };

    // Kept lowercase inside a title ("Lock Port to UniFi Device").
    private static readonly HashSet<string> MinorWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "by", "for", "in", "of", "or", "per", "the", "to", "via",
    };

    // UniFi enum values shown title-cased (On, None, Force Authorized). Any other value, names included, is left as is.
    private static readonly HashSet<string> EnumValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "off", "none", "all", "auto", "enabled", "disabled", "native", "customize", "block", "allow", "reject",
        "optional", "required", "true", "false", "manual",
    };

    private static readonly Dictionary<string, string> Bands = new(StringComparer.Ordinal)
    {
        ["ng"] = "2.4 GHz", ["na"] = "5 GHz", ["6e"] = "6 GHz",
    };

    /// <summary>Label for a field path ("port_overrides[port_idx=4].name" becomes "Port 4 name").</summary>
    public static string LabelFor(string path) => LabelFor(path, null, null);

    /// <summary>
    /// Label for a field path, naming a switch port by the name given to it in <paramref name="after"/>,
    /// else <paramref name="before"/> ("Port 2 (Desk) Lock Port to UniFi Device").
    /// </summary>
    public static string LabelFor(string path, JsonObject? after, JsonObject? before = null)
    {
        var m = KeyedPath().Match(path);
        if (m.Success)
        {
            var (array, key, value, rest) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value.TrimStart('.'));
            var element = array switch
            {
                "port_overrides" => (PortName(after, value) ?? PortName(before, value)) is { } portName ? $"Port {value} ({portName})" : $"Port {value}",
                "radio_table" => Bands.TryGetValue(value, out var band) ? band : value,
                "ethernet_overrides" => value,
                _ => $"{Humanize(array)} {value}",
            };
            return rest.Length == 0 ? element : $"{element} {LabelFor(rest)}";
        }
        var leaf = path.Split('.')[^1];
        var head = path.Split('.')[0];
        if (Labels.TryGetValue(leaf, out var label)) return label;
        if (head != leaf && Labels.TryGetValue(head, out var headLabel)) return $"{headLabel} {Humanize(leaf)}";
        return Humanize(leaf);
    }

    // A port's given name; UniFi's default "Port N" adds nothing to the label.
    private static string? PortName(JsonObject? device, string portIdx) =>
        device?["port_overrides"] is JsonArray ports
        && ports.OfType<JsonObject>().FirstOrDefault(p => p["port_idx"]?.ToString() == portIdx)?["name"]?.ToString() is { Length: > 0 } name
        && !string.Equals(name, $"Port {portIdx}", StringComparison.OrdinalIgnoreCase)
            ? name
            : null;

    [GeneratedRegex(@"^([a-z_]+)\[([a-z_]+)=([^\]]+)\](.*)$")]
    private static partial Regex KeyedPath();

    private static string Humanize(string field)
    {
        var s = field.Replace("_", " ").Trim();
        s = s.EndsWith(" id", StringComparison.Ordinal) ? s[..^3] : s.EndsWith(" ids", StringComparison.Ordinal) ? s[..^4] + "s" : s;
        s = s.Replace("networkconf", "network").Replace("portconf", "port profile");
        // The value already says on or off, so a trailing "enabled" adds nothing ("Flow Control", not "Flow control enabled").
        if (s.EndsWith(" enabled", StringComparison.Ordinal)) s = s[..^8];
        return s.Length == 0 ? field : Title(s);
    }

    /// <summary>Title Case with UniFi's shorthand spelled out and acronyms kept ("dot1x ctrl" becomes "802.1X Control").</summary>
    internal static string Title(string words)
    {
        var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var w = parts[i];
            parts[i] = Words.TryGetValue(w, out var known) ? known
                : i > 0 && MinorWords.Contains(w) ? w
                : char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..];
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// A stored value as shown: UniFi's enum strings title-cased ("on" to "On", "force_authorized" to "Force Authorized").
    /// Names, addresses, and numbers are left exactly as stored.
    /// </summary>
    public static string? DisplayValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var token = value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                    && (value.All(c => !char.IsUpper(c)) || value.All(c => !char.IsLower(c)));
        return token && (EnumValues.Contains(value) || (value.Contains('_') && char.IsAsciiLetter(value[0])))
            ? Title(value.Replace('_', ' ').ToLowerInvariant())
            : value;
    }

    /// <summary>
    /// Rows to show for a change, simplified: when a port moves from manual settings to a port profile, every
    /// override it cleared now comes from the profile, so only the Port Profile row is kept for that port.
    /// </summary>
    public static List<StoredField> ForDisplay(IEnumerable<StoredField> fields)
    {
        var list = fields.ToList();
        var profiled = list
            .Where(f => f.Path.EndsWith(".portconf_id", StringComparison.Ordinal) && IsNone(f.Old) && !IsNone(f.New))
            .Select(f => f.Path[..^".portconf_id".Length])
            .ToHashSet(StringComparer.Ordinal);
        return list
            .Where(f => !(IsNone(f.New) && !f.Secret && profiled.Any(p => f.Path.StartsWith(p + ".", StringComparison.Ordinal))))
            .Select(f => new StoredField
            {
                Path = f.Path,
                Label = f.Path.StartsWith('_') ? Title(f.Label) : f.Label,
                Old = DisplayValue(f.Old),
                New = DisplayValue(f.New),
                Secret = f.Secret,
            })
            .ToList();
    }

    private static bool IsNone(string? v) => string.IsNullOrEmpty(v) || string.Equals(v, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>A label for a sentence: Title Case words lowercased, acronyms and UniFi's own names kept.</summary>
    private static string Sentence(string label, bool keepFirst) =>
        string.Join(' ', label.Split(' ').Select((w, i) =>
            (i > 0 || !keepFirst) && w != "Wi-Fi" && w.Split('-').All(p => p.Length > 1 && char.IsUpper(p[0]) && p[1..].All(char.IsLower))
                ? w.ToLowerInvariant()
                : w));

    /// <summary>WLAN security as UniFi Network names it: Open, WPA2, WPA3, WPA2/WPA3, WPA2 Enterprise.</summary>
    public static string WlanSecurity(JsonObject? w)
    {
        if (w == null) return "";
        var security = w["security"]?.ToString();
        if (security is "open") return "Open";
        var wpa3 = w["wpa3_support"] is JsonValue a && a.TryGetValue<bool>(out var b3) && b3;
        var transition = w["wpa3_transition"] is JsonValue t && t.TryGetValue<bool>(out var bt) && bt;
        var mode = (w["wpa_mode"]?.ToString() ?? "wpa2").ToUpperInvariant();
        var label = wpa3 ? (transition ? "WPA2/WPA3" : "WPA3") : mode;
        return security is "wpaeap" ? label + " Enterprise" : label;
    }

    /// <summary>Display text of a value, rendering references as the referenced object's name.</summary>
    public static string Value(JsonNode? node, ConfigIdentityContext? names = null)
    {
        switch (node)
        {
            case null: return "none";
            case JsonValue v when v.TryGetValue<bool>(out var b): return b ? "on" : "off";
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (s.Length == 0) return "none";
                var name = names?.NameOf(s);
                return name != null && name != s ? name : Pretty(s);
            case JsonValue v: return v.ToString();
            case JsonArray arr when arr.Count == 0: return "none";
            case JsonArray arr when arr.All(e => e is JsonValue):
                return string.Join(", ", arr.Select(e => Value(e, names)));
            case JsonArray arr: return $"{arr.Count} items";
            default:
                var json = node.ToJsonString();
                return json.Length > 80 ? json[..77] + "..." : json;
        }
    }

    private static string Pretty(string s) => s switch
    {
        "ALLOW" => "Allow", "BLOCK" => "Block", "REJECT" => "Reject", "wpa2" => "WPA2", "wpa3" => "WPA3",
        "optional" => "Optional", "disabled" => "Disabled", "required" => "Required",
        _ => s,
    };

    /// <summary>The field rows of an object diff, with WLAN security collapsed into one row.</summary>
    public static List<FieldRow> Rows(ObjectDiff diff, ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null)
    {
        var rows = new List<FieldRow>();
        var securityDone = false;
        foreach (var f in diff.Fields)
        {
            var label = LabelFor(f.Path, diff.After, diff.Before);
            if (f.Secret)
            {
                rows.Add(new FieldRow(label, "", "changed", true));
                continue;
            }
            if (diff.Collection == "wlanconf" && label == "Security")
            {
                if (securityDone) continue;
                securityDone = true;
                rows.Add(new FieldRow(SecurityProtocol, WlanSecurity(diff.Before), WlanSecurity(diff.After), false));
                continue;
            }
            rows.Add(new FieldRow(label, Value(f.Old, beforeNames), Value(f.New, afterNames), false));
        }
        return rows;
    }

    /// <summary>Short "what changed" text: one row as "Label: old to new", else the distinct labels.</summary>
    public static string ChangeText(ObjectDiff diff, ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null)
    {
        switch (diff.Action)
        {
            case ConfigChangeAction.Created:
                return "Created";
            case ConfigChangeAction.Deleted:
                return "Deleted";
        }
        return ChangeText(Rows(diff, beforeNames, afterNames));
    }

    /// <summary>The same text from rows as displayed (<see cref="ForDisplay"/>), for changes already stored.</summary>
    public static string ChangeText(IReadOnlyList<StoredField> fields) =>
        ChangeText(fields.Select(f => new FieldRow(f.Label, f.Old ?? "", f.New ?? "", f.Secret)).ToList());

    private static string ChangeText(List<FieldRow> rows)
    {
        // A WLAN security change is the headline; PMF and the rest show in the detail.
        if (rows.FirstOrDefault(r => r.Label == SecurityProtocol) is { } security && security.Old != security.New)
            return $"security {security.Old} to {security.New}";
        if (rows.Count == 1)
        {
            var r = rows[0];
            var label = Sentence(r.Label, keepFirst: true);
            if (r.Secret) return $"{label} changed";
            if (r.Label == "Enabled") return string.Equals(r.New, "off", StringComparison.OrdinalIgnoreCase) ? "disabled" : "enabled";
            if (r.Old.Length + r.New.Length > 50) return $"{label} changed";
            return $"{label}: {r.Old} to {r.New}";
        }
        var labels = rows.Select(r => r.Label).Distinct().Select((l, i) => Sentence(l, keepFirst: i == 0)).ToList();
        if (labels.Count > 4) return $"{string.Join(", ", labels.Take(3))}, and {labels.Count - 3} more";
        return string.Join(", ", labels);
    }

    /// <summary>One summary line: "WLAN Home: security WPA2 to WPA2/WPA3", "Firewall policy Block IoT created".</summary>
    public static string Summary(ObjectDiff diff, ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null)
    {
        var def = ConfigCollections.Get(diff.Collection);
        var noun = def?.Noun ?? diff.Collection;
        return diff.Action switch
        {
            ConfigChangeAction.Created => $"{noun} {diff.Name} created",
            ConfigChangeAction.Deleted => $"{noun} {diff.Name} deleted",
            _ => SummaryLine(noun, diff.Name, ChangeText(diff, beforeNames, afterNames)),
        };
    }

    /// <summary>"Device Desk Switch: port 5 auto-negotiation, ..." for one updated object.</summary>
    public static string SummaryLine(string noun, string name, string changeText) => $"{noun} {name}: {LowerFirst(changeText)}";

    /// <summary>A group summary: the object's line for one, "N objects changed" for several.</summary>
    public static string GroupSummary(IReadOnlyList<ObjectDiff> objects, string? context = null,
        ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null)
    {
        if (objects.Count == 1) return Summary(objects[0], beforeNames, afterNames);
        var collections = objects.Select(o => o.Collection).Distinct().ToList();
        var what = collections.Count == 1
            ? $"{objects.Count} {Plural(ConfigCollections.Get(collections[0])?.Noun ?? collections[0]).ToLowerInvariant()} changed"
            : $"{objects.Count} objects changed";
        return context == null ? what : $"{what} {context}";
    }

    private static string Plural(string noun) =>
        noun.EndsWith("y", StringComparison.Ordinal) && !noun.EndsWith("ey", StringComparison.Ordinal) ? noun[..^1] + "ies"
        : noun.EndsWith("s", StringComparison.Ordinal) ? noun : noun + "s";

    private static string LowerFirst(string s)
    {
        if (s.Length == 0) return s;
        // Keep acronyms (PMF, VLAN, MLO) as they are.
        var firstWord = s.Split(' ', ':')[0];
        return firstWord.Length > 1 && firstWord.All(char.IsUpper) ? s : char.ToLowerInvariant(s[0]) + s[1..];
    }
}
