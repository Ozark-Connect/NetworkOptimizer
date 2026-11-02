using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>The Edit tab's Collections search, counted on the server for the collections not in the editor.</summary>
public static class CcFind
{
    private static readonly Regex SecretToken = new(@"<secret:[^>\s]+>", RegexOptions.CultureInvariant);

    /// <summary>
    /// What people call a collection or list, mapped to the name the YAML gives it (UniFi's API names): a
    /// query that is a key also finds those. One way only. Keys are singular; a query's plural, case, and
    /// "-" or "_" for a space find them too. Names a query already reaches with spaces as "_" (port forwards,
    /// ip aliases) need no entry.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>
    {
        // Collections
        ["vlan"] = ["network"],
        ["client device"] = ["clients"],
        ["unifi device"] = ["devices"],
        ["ssid"] = ["wlan"],
        ["wifi"] = ["wlan"],
        ["wi-fi"] = ["wlan"],
        ["wireless"] = ["wlan"],
        ["wireless network"] = ["wlan"],
        ["firewall rule"] = ["firewall_policies"],
        ["ip group"] = ["firewall_groups"],
        ["address group"] = ["firewall_groups"],
        ["port group"] = ["firewall_groups"],
        ["port forwarding"] = ["port_forwards"],
        ["dns record"] = ["local_dns"],
        ["ddns"] = ["dynamic_dns"],
        ["policy-based route"] = ["traffic_routes"],
        ["policy-based routing"] = ["traffic_routes"],
        ["selective routing"] = ["traffic_routes"],
        ["device-based routing"] = ["traffic_routes"],
        // WAN load balancing (wan_load_balance_type, _weight) and the per-radio loadbalance_enabled.
        // "failover" needs none: wan_failover_priority and the failover-only type hold the word.
        ["load balancing"] = ["load_balance", "loadbalance"],
        ["wan load balancing"] = ["wan_load_balance"],
        ["pbr"] = ["traffic_routes"],
        // Device kinds. The YAML has no device type (type and model are read-only, never exported), so these
        // find the devices collection and the lists only that kind has.
        ["switch"] = ["devices", "port_overrides", "switch_vlan_enabled"],
        ["access point"] = ["devices", "radio_table", "ap_groups", "ap_group_ids", "ap_macs"],
        ["ap"] = ["devices", "radio_table", "ap_groups", "ap_group_ids", "ap_macs"],
        ["gateway"] = ["devices"],
        ["router"] = ["devices", "gateway"],
        // Fields: the numbered server fields (dns1, wan_dns2, dhcpd_dns_3, dhcpdv6_dns_4, ...).
        ["dns server"] = ["dns1", "dns2", "dns_1", "dns_2", "dns_3", "dns_4"],
        // A client's DHCP reservation. "fixed ip" needs none: the space rule finds fixed_ip and use_fixedip.
        ["reservation"] = ["fixed_ip", "use_fixedip"],
        ["dhcp reservation"] = ["fixed_ip", "use_fixedip"],
        ["static ip"] = ["fixed_ip", "use_fixedip"],
        // DHCP (UniFi's server fields are dhcpd_*, dhcpdv6_*)
        ["dhcp server"] = ["dhcpd"],
        ["dhcp range"] = ["dhcpd_start", "dhcpd_stop", "dhcpdv6_start", "dhcpdv6_stop"],
        ["dhcp pool"] = ["dhcpd_start", "dhcpd_stop", "dhcpdv6_start", "dhcpdv6_stop"],
        ["pxe"] = ["dhcpd_boot"],
        ["network boot"] = ["dhcpd_boot"],
        ["dhcp snooping"] = ["dhcp_snoop"],
        ["rogue dhcp"] = ["dhcpguard"],
        ["inform url"] = ["override_inform_host"],
        // IPv6
        ["prefix delegation"] = ["ipv6_pd", "dhcpv6_pd", "delegation"],
        ["router advertisement"] = ["ipv6_ra"],
        // Wi-Fi radios: UniFi names the bands ng (2.4 GHz), na (5 GHz), and 6e.
        ["2.4 ghz"] = ["radio: ng", "channels_ng", "dtim_ng", "ht_modes_ng", "minrate_ng", "no2ghz"],
        ["2.4ghz"] = ["radio: ng", "channels_ng", "dtim_ng", "ht_modes_ng", "minrate_ng", "no2ghz"],
        ["5 ghz"] = ["radio: na", "channels_na", "dtim_na", "ht_modes_na", "minrate_na", "roaming_assistant_na"],
        ["5ghz"] = ["radio: na", "channels_na", "dtim_na", "ht_modes_na", "minrate_na", "roaming_assistant_na"],
        ["6 ghz"] = ["radio: 6e", "channels_6e", "dtim_6e", "ht_modes_6e", "roaming_assistant_6e"],
        ["6ghz"] = ["radio: 6e", "channels_6e", "dtim_6e", "ht_modes_6e", "roaming_assistant_6e"],
        ["channel width"] = ["channel_width", "ht_modes"],
        ["transmit power"] = ["tx_power"],
        ["minimum rssi"] = ["min_rssi"],
        ["minimum data rate"] = ["minrate"],
        ["min data rate"] = ["minrate"],
        ["802.11r"] = ["fast_roaming"],
        ["802.11v"] = ["bss_transition"],
        ["multicast enhancement"] = ["mcastenhance"],
        ["hidden ssid"] = ["hide_ssid"],
        ["captive portal"] = ["portal"],
        ["guest portal"] = ["portal"],
        ["client isolation"] = ["l2_isolation"],
        ["wpa2"] = ["wpa_mode"],
        ["encryption"] = ["wpa_enc", "wpa_mode"],
        ["protected management frame"] = ["pmf_mode"],
        ["multi link operation"] = ["mlo"],
        ["multi link"] = ["mlo"],
        ["passpoint"] = ["hotspot2conf"],
        ["hotspot 2.0"] = ["hotspot2conf"],
        ["lock to ap"] = ["fixed_ap"],
        ["ap lock"] = ["fixed_ap"],
        // Switching
        ["native vlan"] = ["native_networkconf_id", "native_network"],
        ["untagged vlan"] = ["native_networkconf_id", "native_network"],
        ["voice vlan"] = ["voice_networkconf_id"],
        ["trunk"] = ["tagged_vlan_mgmt", "excluded_networkconf_ids"],
        ["802.1x"] = ["dot1x"],
        ["spanning tree"] = ["stp_"],
        ["storm control"] = ["stormctrl"],
        ["power over ethernet"] = ["poe_mode"],
        ["energy efficient ethernet"] = ["eee_enabled"],
        ["autonegotiation"] = ["autoneg"],
        ["auto negotiation"] = ["autoneg"],
        ["etherlighting"] = ["ether_lighting"],
        ["lcd"] = ["lcm_"],
        ["screen"] = ["lcm_"],
        ["ptp"] = ["precision_time_protocol"],
        // Gateway and security
        ["smart queue"] = ["wan_smartq"],
        ["smartqueue"] = ["wan_smartq"],
        ["sqm"] = ["wan_smartq"],
        ["aqm"] = ["wan_smartq"],
        ["fq codel"] = ["wan_smartq"],
        // "wan ip" needs none: the space rule finds wan_ip and wan_ip_aliases.
        ["public ip"] = ["wan_ip"],
        // Rate limits, Smart Queues rates, and Wi-Fi channel width. Not radio_table's "ht:" (its width): as a
        // term it also matches "weight:".
        ["bandwidth"] = ["download_limit", "upload_limit", "qos_rate_max", "wan_smartq_down_rate", "wan_smartq_up_rate", "channel_width", "ht_modes"],
        ["bw"] = ["download_limit", "upload_limit", "qos_rate_max", "wan_smartq_down_rate", "wan_smartq_up_rate", "channel_width", "ht_modes"],
        ["bandwidth limit"] = ["download_limit", "upload_limit", "qos_rate_max"],
        ["speed limit"] = ["download_limit", "upload_limit", "qos_rate_max"],
        ["mss clamping"] = ["mss_clamp"],
        ["deep packet inspection"] = ["dpi"],
        ["intrusion prevention"] = ["ips"],
        ["intrusion detection"] = ["ips"],
        ["threat management"] = ["ips"],
        ["geoblocking"] = ["regions", "countries"],
        ["geo blocking"] = ["regions", "countries"],
        ["geo ip"] = ["regions", "countries"],
        ["doh"] = ["sdns_stamp", "server_names", "custom_servers"],
        ["dns over https"] = ["sdns_stamp", "server_names", "custom_servers"],
        ["encrypted dns"] = ["sdns_stamp", "server_names", "custom_servers"],
        ["bonjour"] = ["mdns"],
        ["multicast dns"] = ["mdns"],
        ["data cap"] = ["data_limit", "data_soft_limit", "data_warning_threshold"],
        ["auto update"] = ["auto_upgrade"],
        ["automatic update"] = ["auto_upgrade"],
        ["firmware update"] = ["auto_upgrade", "firmware_channel"],
        // Lists inside objects
        ["switch port"] = ["port_overrides"],
        ["lag"] = ["link_aggregation_groups"],
        ["link aggregation"] = ["link_aggregation_groups"],
        ["ppsk"] = ["private_preshared_keys"],
        ["psk"] = ["private_preshared_keys"],
        ["wpa3"] = ["sae_groups", "sae_psk"],
        ["radius server"] = ["auth_servers", "acct_servers"],
        ["secondary ip"] = ["ip_aliases"],
        ["additional ip"] = ["ip_aliases"],
        ["allowlist"] = ["whitelist"],
        ["blocklist"] = ["blocked_sites", "blocked_tld"],
        ["country"] = ["regions", "countries"],
        ["cellular"] = ["sim"],
        ["lte"] = ["sim"],
    }.ToDictionary(a => AliasKey(a.Key), a => a.Value);

    /// <summary>
    /// The texts a query finds: the query and its aliases, longest first so a longer one wins where two
    /// overlap. The editor (findMatches in cc-editor.js) gets this list, so both sides match the same terms.
    /// </summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        var q = Regex.Replace(query.Trim(), @"\s+", " ");
        if (q.Length == 0) return [];
        var terms = new List<string> { q };
        // A plural query finds the singular too ("VLANs" finds "vlan: 20"), along with the aliases. Not a
        // singular under four letters: "ap" from "APs" or "lag" from "LAGs" sits inside half the field names.
        var key = AliasKey(q);
        if (Aliases.TryGetValue(key, out var aliases))
            foreach (var term in key.Length >= 4 ? aliases.Prepend(key) : aliases)
                if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        // A Wi-Fi channel width ("bw80", "160 MHz"): the YAML holds it as a number, on channel_width and radio_table's ht.
        if (Regex.Match(key, @"^(?:bw ?(\d{2,3})|(\d{2,3}) ?mhz)$") is { Success: true } width)
        {
            var mhz = width.Groups[1].Success ? width.Groups[1].Value : width.Groups[2].Value;
            terms.Add($"channel_width: {mhz}");
            terms.Add($"ht: {mhz}");
        }
        return terms.OrderByDescending(t => t.Length).ToList();
    }

    // Lowercase, singular, and "-" or "_" as a space: "VLANs", "Switches", "Client Devices", and
    // "policy-based route" look up their keys. "wireless" is left whole.
    private static string AliasKey(string q)
    {
        var key = Regex.Replace(q.ToLowerInvariant(), @"[\s_-]+", " ").Trim();
        if (Regex.IsMatch(key, "(ch|sh|x)es$")) return key[..^2];
        return key.EndsWith('s') && !key.EndsWith("ss", StringComparison.Ordinal) ? key[..^1] : key;
    }

    /// <summary>
    /// A term as a pattern: literal text, except a space, "_", or "-" matches any of the three or nothing, as
    /// the YAML writes "port forward" as port_forwards, "user group" as usergroup, and "nat-pmp" as nat_pmp.
    /// Mirrors termPattern in cc-editor.js.
    /// </summary>
    public static string TermPattern(string term) =>
        Regex.Escape(Regex.Replace(term, "[ _-]+", " ").Trim()).Replace("\\ ", "[ _-]?");

    /// <summary>
    /// Case-insensitive, non-overlapping matches of a query's <see cref="Terms"/> in a collection's YAML, which
    /// holds field names and values alike. Masked secret tokens are skipped. Must agree with findMatches in
    /// cc-editor.js, which highlights the open collection.
    /// </summary>
    public static int Count(string text, string query)
    {
        var terms = Terms(query);
        if (string.IsNullOrEmpty(text) || terms.Count == 0) return 0;
        var tokens = SecretToken.Matches(text).Select(m => (From: m.Index, To: m.Index + m.Length)).ToList();
        // A query of only "_" or "-" leaves an empty pattern, which would match at every position.
        var patterns = terms.Select(TermPattern).Where(p => p.Length > 0).ToList();
        if (patterns.Count == 0) return 0;
        var pattern = string.Join("|", patterns);
        var count = 0;
        foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int from = m.Index, to = m.Index + m.Length;
            if (!tokens.Any(t => from < t.To && to > t.From)) count++;
        }
        return count;
    }
}
