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
    Func<string, IEnumerable<string>> Parse);

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

    public static readonly IReadOnlyList<PublicBlockList> All =
    [
        new(SpamhausDrop, "Spamhaus DROP",
            "Networks run by spammers and cyber-criminals, or hijacked from their owners (includes the former EDROP).",
            ["https://www.spamhaus.org/drop/drop_v4.json", "https://www.spamhaus.org/drop/drop_v6.json"],
            TimeSpan.FromHours(1), DefaultOn: true, Outbound: true,
            "Spamhaus DROP, (c) The Spamhaus Project", "https://www.spamhaus.org/blocklists/do-not-route-or-peer/",
            ParseSpamhausJson),
        new(Feodo, "abuse.ch Feodo Tracker",
            "Botnet command-and-control servers that are active now.",
            ["https://feodotracker.abuse.ch/downloads/ipblocklist_recommended.txt"],
            TimeSpan.FromMinutes(15), DefaultOn: false, Outbound: true,
            "abuse.ch Feodo Tracker", "https://feodotracker.abuse.ch/blocklist/",
            ParseIpLines),
        new(CinsArmy, "CINS Army",
            "Addresses with a poor reputation across Sentinel IPS sensors: mostly scanners and attackers.",
            ["https://cinsscore.com/list/ci-badguys.txt"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "CINS Army, Collective Intelligence Network Security", "https://cinsscore.com/",
            ParseIpLines),
        new(DShield, "DShield Top 20",
            "The 20 /24 networks that attacked the most in the last three days, so neighbors on shared hosting are blocked too.",
            ["https://feeds.dshield.org/block.txt"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "SANS Internet Storm Center, DShield", "https://isc.sans.edu/feeds_doc.html",
            ParseDShield),
        new(BlocklistDeStrong, "blocklist.de Strong IPs",
            "Addresses reported for repeated attacks on blocklist.de's servers in the last 48 hours.",
            ["https://lists.blocklist.de/lists/strongips.txt"],
            TimeSpan.FromHours(1), DefaultOn: false, Outbound: false,
            "blocklist.de", "https://www.blocklist.de/en/export.html",
            ParseIpLines),
        new(IpsumLevel3, "IPsum (level 3)",
            "Addresses on at least three of 30+ public blocklists, updated daily.",
            ["https://raw.githubusercontent.com/stamparm/ipsum/master/levels/3.txt"],
            TimeSpan.FromHours(6), DefaultOn: false, Outbound: false,
            "IPsum by Miroslav Stampar", "https://github.com/stamparm/ipsum",
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
