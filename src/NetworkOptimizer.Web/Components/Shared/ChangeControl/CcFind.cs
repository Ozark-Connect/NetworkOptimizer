using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Components.Shared.ChangeControl;

/// <summary>The Edit tab's Collections search, counted on the server for the collections not in the editor.</summary>
public static class CcFind
{
    private static readonly Regex SecretToken = new(@"<secret:[^>\s]+>", RegexOptions.CultureInvariant);

    /// <summary>
    /// What people call a collection or list, mapped to the name the YAML gives it (UniFi's API names): a
    /// query that is a key also finds those. One way only. Keys are singular and lowercase; a query's plural
    /// finds them too. Names a query already reaches with spaces as "_" (port forwards, ip aliases) need no entry.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new()
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
        ["policy based route"] = ["traffic_routes"],
        ["pbr"] = ["traffic_routes"],
        // Lists inside objects
        ["access point"] = ["ap_groups", "ap_group_ids", "ap_macs"],
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
        ["country"] = ["regions"],
        ["cellular"] = ["sim"],
        ["lte"] = ["sim"],
    };

    /// <summary>
    /// The texts a query finds: the query and its aliases, longest first so a longer one wins where two
    /// overlap. The editor (findMatches in cc-editor.js) gets this list, so both sides match the same terms.
    /// </summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        var q = Regex.Replace(query.Trim(), @"\s+", " ");
        if (q.Length == 0) return [];
        var terms = new List<string> { q };
        // A plural query finds the singular too ("VLANs" finds "vlan: 20"), along with the aliases.
        var key = AliasKey(q);
        if (Aliases.TryGetValue(key, out var aliases))
            foreach (var term in aliases.Prepend(key))
                if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
        return terms.OrderByDescending(t => t.Length).ToList();
    }

    // Lowercase and singular: "VLANs" and "Client Devices" look up their keys. "wireless" is left whole.
    private static string AliasKey(string q)
    {
        var key = q.ToLowerInvariant();
        return key.EndsWith('s') && !key.EndsWith("ss", StringComparison.Ordinal) ? key[..^1] : key;
    }

    /// <summary>
    /// A term as a pattern: literal text, except a space also matches "_", "-", or nothing, as the YAML
    /// writes "port forward" as port_forwards and "user group" as usergroup. Mirrors termPattern in cc-editor.js.
    /// </summary>
    public static string TermPattern(string term) => Regex.Escape(term).Replace("\\ ", "[ _-]?");

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
        var pattern = string.Join("|", terms.Select(TermPattern));
        var count = 0;
        foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            int from = m.Index, to = m.Index + m.Length;
            if (!tokens.Any(t => from < t.To && to > t.From)) count++;
        }
        return count;
    }
}
