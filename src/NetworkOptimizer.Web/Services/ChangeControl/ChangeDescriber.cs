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
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["enabled"] = "Enabled", ["name"] = "Name", ["action"] = "Action", ["description"] = "Description",
        ["security"] = "Security", ["wpa_mode"] = "Security", ["wpa3_support"] = "Security", ["wpa3_transition"] = "Security",
        ["wpa_enc"] = "Security", ["pmf_mode"] = "PMF", ["x_passphrase"] = "Passphrase", ["x_iapp_key"] = "Fast roaming key",
        ["native_networkconf_id"] = "Native network", ["tagged_networkconf_ids"] = "Tagged networks",
        ["excluded_networkconf_ids"] = "Tagged networks", ["tagged_vlan_mgmt"] = "Tagged VLAN management",
        ["networkconf_id"] = "Network", ["network_id"] = "Network", ["network_ids"] = "Networks",
        ["ip_subnet"] = "Subnet", ["vlan"] = "VLAN", ["vlan_enabled"] = "VLAN", ["mdns_enabled"] = "mDNS",
        ["igmp_snooping"] = "IGMP snooping", ["dhcpd_enabled"] = "DHCP", ["dhcpd_dns_enabled"] = "DHCP DNS",
        ["wlan_bands"] = "Bands", ["mlo_enabled"] = "MLO", ["hide_ssid"] = "Hidden", ["ap_group_ids"] = "AP groups",
        ["group_members"] = "Members", ["fixed_ip"] = "Fixed IP", ["use_fixedip"] = "Fixed IP", ["note"] = "Note",
        ["mgmt_network_id"] = "Management network", ["config_network"] = "IP settings", ["led_override"] = "LED",
        ["stp_priority"] = "STP priority", ["poe_mode"] = "PoE", ["op_mode"] = "Port mode", ["forward"] = "Port mode",
        ["portconf_id"] = "Port profile", ["value"] = "Value", ["record_type"] = "Type", ["key"] = "Hostname",
        ["source"] = "Source", ["destination"] = "Destination", ["schedule"] = "Schedule", ["logging"] = "Logging",
        ["protocol"] = "Protocol", ["ip_version"] = "IP version", ["channel"] = "channel", ["ht"] = "channel width",
        ["tx_power_mode"] = "TX power", ["tx_power"] = "TX power", ["min_rssi"] = "Min RSSI", ["min_rssi_enabled"] = "Min RSSI",
        ["dhcpd_dns_1"] = "DNS server", ["dhcpd_dns_2"] = "DNS server", ["wan_dns1"] = "DNS server", ["wan_dns2"] = "DNS server",
        ["port_security_enabled"] = "Port Security", ["port_security_mac_address"] = "MAC Address Filter",
        ["trusted_port_mac"] = "Lock Port to UniFi Device",
    };

    // UniFi Network's own setting names: kept as written after a port or radio prefix, not lowercased.
    private static readonly HashSet<string> UniFiNames = new(StringComparer.Ordinal)
    {
        "Port Security", "MAC Address Filter", "Lock Port to UniFi Device",
    };

    private static readonly Dictionary<string, string> Bands = new(StringComparer.Ordinal)
    {
        ["ng"] = "2.4 GHz", ["na"] = "5 GHz", ["6e"] = "6 GHz",
    };

    /// <summary>Label for a field path ("port_overrides[port_idx=4].name" becomes "Port 4 name").</summary>
    public static string LabelFor(string path)
    {
        var m = KeyedPath().Match(path);
        if (m.Success)
        {
            var (array, key, value, rest) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value.TrimStart('.'));
            var element = array switch
            {
                "port_overrides" => $"Port {value}",
                "radio_table" => Bands.TryGetValue(value, out var band) ? band : value,
                "ethernet_overrides" => value,
                _ => $"{Humanize(array)} {value}",
            };
            if (rest.Length == 0) return element;
            var inner = LabelFor(rest);
            return $"{element} {(UniFiNames.Contains(inner) ? inner : inner.ToLowerInvariant())}";
        }
        var leaf = path.Split('.')[^1];
        var head = path.Split('.')[0];
        if (Labels.TryGetValue(leaf, out var label)) return label;
        if (head != leaf && Labels.TryGetValue(head, out var headLabel)) return $"{headLabel} {Humanize(leaf).ToLowerInvariant()}";
        return Humanize(leaf);
    }

    [GeneratedRegex(@"^([a-z_]+)\[([a-z_]+)=([^\]]+)\](.*)$")]
    private static partial Regex KeyedPath();

    private static string Humanize(string field)
    {
        var s = field.Replace("_", " ").Trim();
        s = s.EndsWith(" id", StringComparison.Ordinal) ? s[..^3] : s.EndsWith(" ids", StringComparison.Ordinal) ? s[..^4] + "s" : s;
        s = s.Replace("networkconf", "network").Replace("portconf", "port profile");
        return s.Length == 0 ? field : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..];
    }

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
            var label = LabelFor(f.Path);
            if (f.Secret)
            {
                rows.Add(new FieldRow(label, "", "changed", true));
                continue;
            }
            if (diff.Collection == "wlanconf" && label == "Security")
            {
                if (securityDone) continue;
                securityDone = true;
                rows.Add(new FieldRow("Security protocol", WlanSecurity(diff.Before), WlanSecurity(diff.After), false));
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
        var rows = Rows(diff, beforeNames, afterNames);
        // A WLAN security change is the headline; PMF and the rest show in the detail.
        if (rows.FirstOrDefault(r => r.Label == "Security protocol") is { } security && security.Old != security.New)
            return $"security {security.Old} to {security.New}";
        if (rows.Count == 1)
        {
            var r = rows[0];
            if (r.Secret) return $"{r.Label} changed";
            if (r.Label == "Enabled") return r.New == "off" ? "disabled" : "enabled";
            if (r.Old.Length + r.New.Length > 50) return $"{r.Label} changed";
            return $"{r.Label}: {r.Old} to {r.New}";
        }
        var labels = rows.Select(r => r.Secret ? r.Label.ToLowerInvariant() : r.Label).Distinct().ToList();
        if (labels.Count > 4) return $"{string.Join(", ", labels.Take(3))}, and {labels.Count - 3} more";
        return string.Join(", ", labels.Select((l, i) => i == 0 ? l : l.ToLowerInvariant()));
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
            _ => $"{noun} {diff.Name}: {LowerFirst(ChangeText(diff, beforeNames, afterNames))}",
        };
    }

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
