using System.Text.Json;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Names for UniFi Network's DPI application and category ids, which no API serves. Taken from the
/// tables the Network app ships for its own UI: the legacy <c>dynamic.dpi.js</c> (catalog 1.406), with
/// names and categories from the React UI's newer catalog (2.128) layered on top, since that one drops
/// ids older signatures still report. The legacy table also gives, for the few applications that have
/// one, the domain whose favicon the app shows and the Font Awesome brand class it uses (mapped to
/// Font Awesome 6 names). Refresh by re-parsing newer bundles into <c>Resources/dpi-catalog.json</c>.
///
/// An application key packs the category into the high half: <c>(category &lt;&lt; 16) | application</c>,
/// which is how the traffic endpoints' separate <c>category</c> / <c>application</c> ids map onto it.
/// </summary>
public static class DpiCatalog
{
    private sealed record Entry(string N, string? D, string? I);
    private sealed record Catalog(string Version, Dictionary<string, string> Categories, Dictionary<string, Entry> Applications);

    private static readonly Lazy<Catalog> _catalog = new(Load);
    private static readonly Lazy<HashSet<string>> _iconDomains = new(() =>
        new HashSet<string>(_catalog.Value.Applications.Values.Where(e => e.D != null).Select(e => e.D!), StringComparer.OrdinalIgnoreCase));

    private static Catalog Load()
    {
        using var stream = typeof(DpiCatalog).Assembly.GetManifestResourceStream("dpi-catalog.json")
            ?? throw new InvalidOperationException("dpi-catalog.json is not embedded");
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("dpi-catalog.json did not parse");
        foreach (var (key, entry) in catalog.Applications.ToList())
            if (BrandCasing(entry.N) is { } fixedName) catalog.Applications[key] = entry with { N = fixedName };
        // "Apple.com" beside "Apple" splits one app in two wherever apps group: the bare name wins.
        var bare = new HashSet<string>(catalog.Applications.Values.Select(e => e.N).Concat(NamedByDomain), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, entry) in catalog.Applications.ToList())
        {
            var dot = entry.N.LastIndexOf('.');
            if (dot > 0 && entry.N[(dot + 1)..] is "com" or "net" or "org" or "tv" && bare.FirstOrDefault(n => n.Equals(entry.N[..dot], StringComparison.OrdinalIgnoreCase)) is { } name)
                catalog.Applications[key] = entry with { N = name };
        }
        return catalog;
    }

    /// <summary>Names Live Traffic gives by domain or owner that the catalog also has with a ".com".</summary>
    private static readonly string[] NamedByDomain = ["Apple", "Akamai"];

    /// <summary>
    /// Brands as their owners spell them, where UniFi Network's catalog does not: corrected by whole
    /// word, so "Youtube Kids" reads "YouTube Kids". Corrected here rather than in the JSON so a catalog
    /// refresh keeps them.
    /// </summary>
    private static readonly Dictionary<string, string> BrandWords = new(StringComparer.Ordinal)
    {
        ["Youtube"] = "YouTube",
        ["Facetime"] = "FaceTime",
        ["XBOX"] = "Xbox",
        ["Airplay"] = "AirPlay",
        ["Sharepoint"] = "SharePoint",
        ["WOW"] = "WoW",
    };

    private static string? BrandCasing(string name)
    {
        var words = name.Split(' ');
        var changed = false;
        for (var i = 0; i < words.Length; i++)
        {
            if (BrandWords.TryGetValue(words[i], out var brand)) { words[i] = brand; changed = true; }
        }
        return changed ? string.Join(' ', words) : null;
    }

    /// <summary>The catalog version the names come from.</summary>
    public static string Version => _catalog.Value.Version;

    public static int Key(int category, int application) => (category << 16) | (application & 0xffff);

    /// <summary>The application's name, or null when the catalog does not know the id.</summary>
    public static string? AppName(int category, int application) =>
        _catalog.Value.Applications.TryGetValue(Key(category, application).ToString(), out var e) ? e.N : null;

    /// <summary>The category's name, or null when the catalog does not know the id.</summary>
    public static string? CategoryName(int category) =>
        _catalog.Value.Categories.TryGetValue(category.ToString(), out var n) ? n : null;

    /// <summary>The domain whose favicon the Network app shows for this application, if it has one.</summary>
    public static string? IconDomain(int category, int application) =>
        _catalog.Value.Applications.TryGetValue(Key(category, application).ToString(), out var e) ? e.D : null;

    /// <summary>
    /// Applications the catalog leaves without a mark but that deserve one, by name. Protocols and
    /// tools mostly; brands the catalog missed are here too.
    /// </summary>
    private static readonly Dictionary<string, string> IconByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SSL/TLS"] = "fa-solid fa-lock",
        ["QUIC"] = "fa-solid fa-bolt",
        ["Wi-Fi Calling"] = "fa-solid fa-phone",
        ["Speedtest.net"] = "fa-solid fa-gauge-high",
        ["Backblaze"] = "no-icon no-icon-backblaze",
        ["AWS"] = "fa-brands fa-aws",
        ["Microsoft Windows Update"] = "fa-brands fa-windows",
        // No Outlook mark in Font Awesome or Simple Icons: Microsoft's, as its other services carry.
        ["Outlook.com"] = "fa-brands fa-microsoft",
        ["Roku"] = "no-icon no-icon-roku",
        ["Xbox"] = "fa-brands fa-xbox",
        ["FaceTime"] = "fa-brands fa-apple",
        ["iTunes"] = "fa-brands fa-itunes",
        // Marks Font Awesome lacks: app.css draws the no-icon-* classes (Simple Icons, CC0).
        ["WireGuard"] = "no-icon no-icon-wireguard",
        ["OpenVPN"] = "no-icon no-icon-openvpn",
        ["MQTT"] = "no-icon no-icon-mqtt",
        ["MQTT-TLS"] = "no-icon no-icon-mqtt",
        ["Elasticsearch"] = "no-icon no-icon-elasticsearch",
        ["Tailscale"] = "no-icon no-icon-tailscale",
        // Network Optimizer's own traffic: our favicon, in color (app-mark, not a mask).
        ["Network Optimizer"] = "app-mark app-mark-netopt",
        ["AP Agent"] = "app-mark app-mark-netopt",
        ["ChatGPT"] = "no-icon no-icon-openai",
        ["OpenAI"] = "no-icon no-icon-openai",
        ["Claude"] = "no-icon no-icon-claude",
        ["Anthropic"] = "no-icon no-icon-anthropic",
        // Local protocols DomainAppCatalog.LanApp names by port.
        ["Google Cast"] = "fa-brands fa-chromecast",
        ["Midea"] = "fa-solid fa-house-signal",
        ["LG webOS TV"] = "fa-solid fa-tv",
        ["HomeKit"] = "fa-solid fa-house",
        ["iperf3"] = "fa-solid fa-gauge-high",
        // Mail protocols named by their port: the catalog's mail mark.
        ["SMTP"] = "fa-solid fa-envelope",
        ["SMTPS"] = "fa-solid fa-envelope",
        ["SMTP Submit"] = "fa-solid fa-envelope",
        ["POP3"] = "fa-solid fa-envelope",
        ["POP3S"] = "fa-solid fa-envelope",
        ["IMAP"] = "fa-solid fa-envelope",
        ["IMAPS"] = "fa-solid fa-envelope",
        // VPN protocols named by their port: the catalog's tunneling mark, not the initial.
        ["IPSec NAT-T"] = "fa-solid fa-shield-halved",
        ["IKE"] = "fa-solid fa-shield-halved",
        ["PPTP"] = "fa-solid fa-shield-halved",
        ["DNS"] = "fa-solid fa-server",
        ["DNS over HTTPS"] = "fa-solid fa-server",
        ["DNS over TLS"] = "fa-solid fa-server",
        ["DNS over QUIC"] = "fa-solid fa-server",
        ["NTP"] = "fa-solid fa-clock",
        ["HTTP"] = "fa-solid fa-globe",
        ["HTTPS"] = "fa-solid fa-globe",
        ["HTTP-Alt"] = "fa-solid fa-globe",
        ["HTTPS-Alt"] = "fa-solid fa-globe",
        ["Web Streaming"] = "fa-solid fa-play",
        ["ICMP"] = "no-icon no-icon-icmp",
        ["eBay"] = "fa-brands fa-ebay",
        ["Discord"] = "fa-brands fa-discord",
        ["TikTok"] = "fa-brands fa-tiktok",
        ["Facebook Messenger"] = "fa-brands fa-facebook-messenger",
        ["Google Drive"] = "fa-brands fa-google-drive",
        ["YouTube Kids"] = "fa-brands fa-youtube",
        ["bilibili"] = "fa-brands fa-bilibili",
        ["Threads"] = "fa-brands fa-threads",
        ["Waze"] = "fa-brands fa-waze",
        ["Trello"] = "fa-brands fa-trello",
        ["Lyft"] = "fa-brands fa-lyft",
        ["Uber"] = "fa-brands fa-uber",
        ["Uber Eats"] = "fa-brands fa-uber",
        ["Reddit"] = "fa-brands fa-reddit",
        ["Docker"] = "fa-brands fa-docker",
        ["Jira"] = "fa-brands fa-jira",
        ["Salesforce"] = "fa-brands fa-salesforce",
        ["MailChimp"] = "fa-brands fa-mailchimp",
        ["Evernote"] = "fa-brands fa-evernote",
        ["Yammer"] = "fa-brands fa-yammer",
        ["LinkedIn Pulse"] = "fa-brands fa-linkedin",
        ["Skype for Business"] = "fa-brands fa-skype",
        ["Viber"] = "fa-brands fa-viber",
        ["LINE"] = "fa-brands fa-line",
        ["TeamSpeak"] = "fa-brands fa-teamspeak",
        ["Weibo"] = "fa-brands fa-weibo",
        ["RenRen"] = "fa-brands fa-renren",
        ["Blogger"] = "fa-brands fa-blogger",
        ["500px"] = "fa-brands fa-500px",
        ["Periscope"] = "fa-brands fa-periscope",
        ["Mix"] = "fa-brands fa-mix",
        ["Mendeley"] = "fa-brands fa-mendeley",
        ["Deezer"] = "fa-brands fa-deezer",
        ["Audible"] = "fa-brands fa-audible",
        ["Napster"] = "fa-brands fa-napster",
        ["Dailymotion"] = "fa-brands fa-dailymotion",
        ["iTunes Festival"] = "fa-brands fa-itunes",
        ["Battle.net"] = "fa-brands fa-battle-net",
        // Blizzard has no mark of its own in Font Awesome or Simple Icons: its games take Battle.net's.
        ["Diablo"] = "fa-brands fa-battle-net",
        ["Diablo3"] = "fa-brands fa-battle-net",
        ["Hearthstone"] = "fa-brands fa-battle-net",
        ["StarCraft2"] = "fa-brands fa-battle-net",
        ["Overwatch"] = "fa-brands fa-battle-net",
        ["WoW Game"] = "fa-brands fa-battle-net",
        // EA's launcher and games take EA's mark, as Blizzard's take Battle.net's.
        ["Electronic Arts"] = "no-icon no-icon-ea",
        ["Origin"] = "no-icon no-icon-ea",
        ["Battlefield"] = "no-icon no-icon-ea",
        ["Star Wars Battlefront"] = "no-icon no-icon-ea",
        ["FIFA"] = "no-icon no-icon-ea",
        ["Opera browser"] = "fa-brands fa-opera",
        ["Opera VPN"] = "fa-brands fa-opera",
        ["Java Applet"] = "fa-brands fa-java",
        ["JAVA Update"] = "fa-brands fa-java",
        ["Bitcoin Core"] = "fa-brands fa-bitcoin",
        ["CPanel"] = "fa-brands fa-cpanel",
        ["Blackberry(SSL)"] = "fa-brands fa-blackberry",
        ["Mozilla Firefox"] = "fa-brands fa-firefox",
        ["Sony PlayStation"] = "fa-brands fa-playstation",
        ["APP Store Connect"] = "fa-brands fa-app-store",
        ["Yandex.Disk"] = "fa-brands fa-yandex",
        ["Yandex.Mail"] = "fa-brands fa-yandex",
        ["Yandex.Browser"] = "fa-brands fa-yandex",
    };

    /// <summary>A brand word anywhere in the name ("Microsoft.com", "Google Drive") is that brand's mark.</summary>
    private static readonly (string Word, string Icon)[] IconByBrandWord =
    {
        ("Microsoft", "fa-brands fa-microsoft"),
        ("Google", "fa-brands fa-google"),
        ("Apple", "fa-brands fa-apple"),
        ("Amazon", "fa-brands fa-amazon"),
        ("Facebook", "fa-brands fa-facebook"),
        ("Yahoo", "fa-brands fa-yahoo"),
        ("Windows", "fa-brands fa-windows"),
        ("Cloudflare", "fa-brands fa-cloudflare"),
        ("Akamai", "no-icon no-icon-akamai"),
        // Valve's own network carries Steam; an app named only by its owner reads "Valve".
        ("Valve", "fa-brands fa-steam"),
        ("Blizzard", "fa-brands fa-battle-net"),
        ("Electronic Arts", "no-icon no-icon-ea"),
        ("Steam", "fa-brands fa-steam"),
    };

    /// <summary>What an application in each category is broadly doing, for one with no mark of its own.</summary>
    private static readonly Dictionary<int, string> IconByCategory = new()
    {
        [0] = "fa-solid fa-comment-dots",       // Instant messengers
        [1] = "fa-solid fa-share-nodes",        // Peer-to-peer networks
        [3] = "fa-solid fa-folder-open",        // File sharing services and tools
        [4] = "fa-solid fa-play",               // Media streaming services
        [5] = "fa-solid fa-envelope",           // Email messaging services
        [6] = "fa-solid fa-phone",              // VoIP services
        [7] = "fa-solid fa-database",           // Database tools
        [8] = "fa-solid fa-gamepad",            // Online games
        [9] = "fa-solid fa-screwdriver-wrench", // Management tools and protocols
        [10] = "fa-solid fa-terminal",          // Remote access terminals
        [11] = "fa-solid fa-shield-halved",     // Tunneling and proxy services
        [12] = "fa-solid fa-chart-line",        // Investment platforms
        [13] = "fa-solid fa-globe",             // Web services
        [14] = "fa-solid fa-shield",            // Security update tools
        [15] = "fa-solid fa-comments",          // Web instant messengers
        [17] = "fa-solid fa-briefcase",         // Business tools
        [18] = "fa-solid fa-network-wired",     // Network protocols
        [19] = "fa-solid fa-network-wired",
        [20] = "fa-solid fa-network-wired",
        [23] = "fa-solid fa-lock",              // Private protocols
        [24] = "fa-solid fa-users",             // Social networks
        [255] = "fa-solid fa-question",         // Unknown
    };

    /// <summary>
    /// The Font Awesome class to show for an application: the catalog's own mark, else a pick by
    /// name, else a brand word in the name, else the category's, else the question mark - a
    /// category the catalog does not know is as unidentified as an application it does not.
    /// </summary>
    public static string IconClass(int category, int application)
    {
        if (_catalog.Value.Applications.TryGetValue(Key(category, application).ToString(), out var e))
        {
            if (e.I != null) return e.I;
            if (IconByName.TryGetValue(e.N, out var byName)) return byName;
            foreach (var (word, icon) in IconByBrandWord)
            {
                if (e.N.Contains(word, StringComparison.OrdinalIgnoreCase)) return icon;
            }
        }
        return IconByCategory.TryGetValue(category, out var byCategory) ? byCategory : IconByCategory[255];
    }

    private static readonly Lazy<Dictionary<string, Entry>> _byName = new(() => _catalog.Value.Applications.Values
        .GroupBy(e => e.N, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.D != null).ThenByDescending(e => e.I != null).First(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The icons for an application known only by name (a domain match, a port service): the catalog
    /// entry of that name, else a pick by name or brand word. Null class when nothing fits, so the
    /// caller shows the initial rather than a question mark for a label that is not an application.
    /// </summary>
    public static (string? Domain, string? Class) IconsForName(string name)
    {
        if (_byName.Value.TryGetValue(name, out var e))
            return (e.D, e.I ?? (IconByName.TryGetValue(e.N, out var c) ? c : BrandIcon(e.N)));
        return (null, IconByName.TryGetValue(name, out var byName) ? byName : BrandIcon(name) ?? ProtocolIcon(name));
    }

    /// <summary>A flow named only by its transport ("TCP", "UDP 3478"): a connection mark for TCP, a datagram mark for UDP.</summary>
    private static string? ProtocolIcon(string name)
    {
        var space = name.IndexOf(' ');
        if (space >= 0 && !name[(space + 1)..].All(char.IsDigit)) return null;
        var proto = space < 0 ? name : name[..space];
        return proto.ToUpperInvariant() switch
        {
            "TCP" => "fa-solid fa-right-left",
            "UDP" => "fa-solid fa-paper-plane",
            _ => null,
        };
    }

    private static string? BrandIcon(string name)
    {
        foreach (var (word, icon) in IconByBrandWord)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return icon;
        }
        return null;
    }

    /// <summary>Whether a domain is one the catalog names, so the icon path cannot be pointed anywhere else.</summary>
    public static bool IsIconDomain(string domain) => _iconDomains.Value.Contains(domain);
}
