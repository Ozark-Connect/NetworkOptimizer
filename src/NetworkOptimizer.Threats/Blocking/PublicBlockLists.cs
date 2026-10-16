using System.Net;
using System.Text.Json;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>A public block list Network Optimizer can apply alongside its own decisions.</summary>
/// <param name="Id">Stable id stored in settings.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One line on what it lists.</param>
/// <param name="Urls">Where to fetch it; each is parsed with <see cref="Parse"/>.</param>
/// <param name="Refresh">How often to fetch, within the source's guidance (abuse.ch asks for 15 minutes or sooner).</param>
/// <param name="DefaultOn">Applied on a site that has not chosen. Only lists whose terms plainly allow any use.</param>
/// <param name="Outbound">
/// Lists hosts nothing on the LAN should ever reach (command servers, criminal-run networks): also blocked
/// outbound, and a device reaching one raises a Public Block List alert. Attacker and scanner lists are
/// inbound only: their addresses are often compromised hosts that also serve legitimate content.
/// </param>
/// <param name="Attribution">Credit the source's terms ask for.</param>
/// <param name="InfoUrl">The source's page.</param>
/// <param name="Parse">Turns one downloaded body into IPs and CIDR ranges.</param>
/// <param name="TokenUser">
/// Set on a list that needs the site's own API token sent as HTTP Basic: the username, with the token as
/// the password. Each URL of a token list is fetched and cached on its own, since the source rate-limits each.
/// </param>
/// <param name="TokenHeader">Set on a list that needs the site's own API token sent in this request header.</param>
/// <param name="TokenName">What the source calls its token, for the token field.</param>
public sealed record PublicBlockList(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Urls,
    TimeSpan Refresh,
    bool DefaultOn,
    bool Outbound,
    string Attribution,
    string InfoUrl,
    Func<string, IEnumerable<string>> Parse,
    string? TokenUser = null,
    string? TokenHeader = null,
    string TokenName = "API token")
{
    /// <summary>Whether the list needs the site's own API token.</summary>
    public bool NeedsToken => TokenUser != null || TokenHeader != null;
}

/// <summary>
/// The public lists on offer, chosen for licenses that allow use in Network Optimizer and for low
/// false positives. Left out on purpose: Emerging Threats compromised IPs (license unresolved) and
/// FireHOL Level 1 (DROP, Feodo, and DShield plus bogons, which include every private range).
/// Applied to the gateway and the WAF, never to Cloudflare: its edge has its own reputation data.
/// </summary>
/// <remarks>
/// Feodo Tracker is off by default: takedowns emptied it, and abuse.ch's terms since November 2025
/// limit free use to not-for-profit purposes. CINS Army has no formal license, so it is opt-in.
/// </remarks>
public static class PublicBlockLists
{
    public const string SpamhausDrop = "spamhaus-drop";
    public const string Feodo = "feodo";
    public const string DShield = "dshield";
    public const string BlocklistDeStrong = "blocklist-de-strong";
    public const string CinsArmy = "cins-army";
    public const string IpsumLevel3 = "ipsum-3";
    public const string TorExits = "tor-exits";
    public const string QFeeds = "qfeeds-malware-ip";
    public const string ThreatFox = "threatfox-ip";

    /// <summary>In the order the Settings card shows them: most useful first.</summary>
    public static readonly IReadOnlyList<PublicBlockList> All =
    [
        new(SpamhausDrop, "Spamhaus DROP",
            "Networks run by spammers and cyber-criminals, or hijacked from their owners (includes the former EDROP).",
            ["https://www.spamhaus.org/drop/drop_v4.json", "https://www.spamhaus.org/drop/drop_v6.json"],
            TimeSpan.FromHours(1), DefaultOn: true, Outbound: true,
            "Spamhaus DROP, (c) The Spamhaus Project", "https://www.spamhaus.org/blocklists/do-not-route-or-peer/",
            ParseSpamhausJson),
        new(ThreatFox, "abuse.ch ThreatFox",
            "Botnet command-and-control servers reported in the last 48 hours (Cobalt Strike, AsyncRAT, Remcos, and more), leaving out hijacked legitimate servers and low-confidence reports. Needs your own abuse.ch Auth Key, which is free.",
            ["https://threatfox.abuse.ch/export/csv/ip-port/recent/"],
            TimeSpan.FromMinutes(15), DefaultOn: false, Outbound: true,
            "abuse.ch ThreatFox", "https://threatfox.abuse.ch/",
            ParseThreatFoxIpPort, TokenHeader: "Auth-Key", TokenName: "abuse.ch Auth Key"),
        // Q-Feeds allows one download per token, per feed, and per IP version in each update window
        // (24 hours on Community, 4 on Plus, 20 minutes on Premium); an early one returns 429, which
        // only means not yet. Hourly attempts follow whatever the token's plan allows.
        new(QFeeds, "Q-Feeds Malware IPs",
            "Malicious hosts, botnets, and command-and-control servers. Needs your own Q-Feeds API token; the Community edition is free.",
            // The full IPv4 feed is over 200,000 entries; limit keeps the newest, as Q-Feeds' own UniFi guide suggests.
            ["https://api.qfeeds.com/api?feed_type=malware_ip&ipv6=0&limit=50000", "https://api.qfeeds.com/api?feed_type=malware_ip&ipv6=only"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: true,
            "Q-Feeds", "https://qfeeds.com/",
            ParseIpLines, TokenUser: "api_token", TokenName: "Q-Feeds API token"),
        new(CinsArmy, "CINS Army",
            "Addresses with a poor reputation across Sentinel IPS sensors: mostly scanners and attackers.",
            ["https://cinsscore.com/list/ci-badguys.txt"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "CINS Army, Collective Intelligence Network Security", "https://cinsscore.com/",
            ParseIpLines),
        new(IpsumLevel3, "IPsum (level 3)",
            "Addresses on at least three of 30+ public blocklists, updated daily.",
            ["https://raw.githubusercontent.com/stamparm/ipsum/master/levels/3.txt"],
            TimeSpan.FromHours(6), DefaultOn: false, Outbound: false,
            "IPsum by Miroslav Stampar", "https://github.com/stamparm/ipsum",
            ParseIpLines),
        new(BlocklistDeStrong, "blocklist.de Strong IPs",
            "Addresses reported for repeated attacks on blocklist.de's servers in the last 48 hours.",
            ["https://lists.blocklist.de/lists/strongips.txt"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "blocklist.de", "https://www.blocklist.de/en/export.html",
            ParseIpLines),
        new(DShield, "DShield Top 20",
            "The 20 /24 networks that attacked the most in the last three days, so neighbors on shared hosting are blocked too.",
            ["https://feeds.dshield.org/block.txt"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "SANS Internet Storm Center, DShield", "https://isc.sans.edu/feeds_doc.html",
            ParseDShield),
        new(Feodo, "abuse.ch Feodo Tracker",
            "Botnet command-and-control servers for a few banking-trojan families. Nearly empty since their takedowns; ThreatFox covers what replaced them.",
            ["https://feodotracker.abuse.ch/downloads/ipblocklist_recommended.txt"],
            TimeSpan.FromMinutes(15), DefaultOn: false, Outbound: true,
            "abuse.ch Feodo Tracker", "https://feodotracker.abuse.ch/blocklist/",
            ParseIpLines),
        new(TorExits, "Tor exit relays",
            "Every Tor exit, legitimate or not. Only for sites that should never be reached over Tor.",
            ["https://check.torproject.org/torbulkexitlist"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "The Tor Project", "https://check.torproject.org/torbulkexitlist",
            ParseIpLines),
    ];

    public static PublicBlockList? Find(string id) => All.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// The note under Turn On Recommended naming the lists it will apply, or null when none are chosen:
    /// Enforce writes them to the gateway, which the button should never do unannounced.
    /// </summary>
    public static string? RecommendedNote(IEnumerable<string> ids)
    {
        var names = ids.Select(Find).OfType<PublicBlockList>().Select(l => l.Name).ToList();
        return names.Count switch
        {
            0 => null,
            1 => $"Includes the public block list {names[0]}.",
            2 => $"Includes the public block lists {names[0]} and {names[1]}.",
            _ => $"Includes the public block lists {string.Join(", ", names[..^1])}, and {names[^1]}.",
        };
    }

    /// <summary>The lists a site uses before it chooses.</summary>
    public static IReadOnlyList<string> DefaultIds => All.Where(l => l.DefaultOn).Select(l => l.Id).ToList();

    /// <summary>Spamhaus's JSON lines: one {"cidr": ...} object per line, plus a closing metadata line.</summary>
    public static IEnumerable<string> ParseSpamhausJson(string body)
    {
        foreach (var line in Lines(body))
        {
            if (!line.StartsWith('{')) continue;
            string? cidr = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("cidr", out var c) && c.ValueKind == JsonValueKind.String)
                    cidr = c.GetString();
            }
            catch (JsonException)
            {
                continue;
            }
            if (Normalize(cidr) is { } n) yield return n;
        }
    }

    /// <summary>One IP or CIDR per line; # starts a comment.</summary>
    public static IEnumerable<string> ParseIpLines(string body)
    {
        foreach (var line in Lines(body))
        {
            var entry = line.Split('#', 2)[0].Trim();
            if (Normalize(entry) is { } n) yield return n;
        }
    }

    /// <summary>
    /// ThreatFox's ip:port CSV, as the IPs alone. Hijacked legitimate servers (is_compromised) and reports
    /// under 75% confidence are left out: blocked outbound, they would cut off whatever else they host.
    /// </summary>
    public static IEnumerable<string> ParseThreatFoxIpPort(string body)
    {
        foreach (var line in Lines(body))
        {
            if (!line.StartsWith('"')) continue;
            var cols = line.Split(',').Select(c => c.Trim().Trim('"')).ToArray();
            // first_seen, ioc_id, ioc_value, ioc_type, threat_type, fk_malware, alias, printable, last_seen, confidence, is_compromised, ...
            if (cols.Length < 11 || cols[3] != "ip:port") continue;
            if (!int.TryParse(cols[9], out var confidence) || confidence < 75) continue;
            if (!string.Equals(cols[10], "False", StringComparison.OrdinalIgnoreCase)) continue;
            var value = cols[2];
            // IPv6 values are bracketed ([2001:db8::1]:443); IPv4 ones end in :port.
            var host = value.StartsWith('[') ? value[1..value.IndexOf(']')] : value[..value.LastIndexOf(':')];
            if (Normalize(host) is { } n) yield return n;
        }
    }

    /// <summary>DShield's block.txt: tab-separated start address, end address, prefix length, ...</summary>
    public static IEnumerable<string> ParseDShield(string body)
    {
        foreach (var line in Lines(body))
        {
            if (line.StartsWith('#')) continue;
            var cols = line.Split('\t');
            if (cols.Length >= 3 && int.TryParse(cols[2], out var bits) && Normalize($"{cols[0]}/{bits}") is { } n)
                yield return n;
        }
    }

    /// <summary>A canonical IP or CIDR (network address, lowercase IPv6), or null when it is neither.</summary>
    public static string? Normalize(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        entry = entry.Trim();
        if (!entry.Contains('/'))
            return BlockAllowlist.Normalize(entry);
        if (!IPNetwork.TryParse(entry, out var network))
        {
            // IPNetwork rejects host bits set; mask them off rather than drop the entry.
            var slash = entry.IndexOf('/');
            if (!IPAddress.TryParse(entry[..slash], out var addr) || !int.TryParse(entry[(slash + 1)..], out var len))
                return null;
            var max = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            if (len < 0 || len > max) return null;
            network = new IPNetwork(Mask(addr, len), len);
        }
        return network.ToString();
    }

    private static IPAddress Mask(IPAddress addr, int len)
    {
        var bytes = addr.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(len - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }

    private static IEnumerable<string> Lines(string body) =>
        body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
