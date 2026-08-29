using System.Text.Json;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Names for UniFi Network's DPI application and category ids, which no API serves. Taken from the
/// table the Network app ships for its own UI (<c>dynamic.dpi.js</c>, catalog version 1.406): names,
/// and for the few applications that have one, the domain whose favicon the app shows and the Font
/// Awesome brand class it uses (mapped to Font Awesome 6 names). Refresh by re-parsing a newer
/// bundle into <c>Resources/dpi-catalog.json</c>.
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
        return JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("dpi-catalog.json did not parse");
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
        ["Backblaze"] = "fa-solid fa-cloud-arrow-up",
        ["Microsoft Windows Update"] = "fa-brands fa-windows",
        ["XBOX"] = "fa-brands fa-xbox",
        ["iTunes"] = "fa-brands fa-itunes",
        ["DNS"] = "fa-solid fa-server",
        ["NTP"] = "fa-solid fa-clock",
        ["HTTP"] = "fa-solid fa-globe",
        ["HTTPS"] = "fa-solid fa-globe",
        ["Web Streaming"] = "fa-solid fa-play",
        ["ICMP"] = "fa-solid fa-satellite-dish",
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
    /// name, else a brand word in the name, else the category's, else null and the caller shows
    /// an initial.
    /// </summary>
    public static string? IconClass(int category, int application)
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
        return IconByCategory.TryGetValue(category, out var byCategory) ? byCategory : null;
    }

    /// <summary>Whether a domain is one the catalog names, so the icon path cannot be pointed anywhere else.</summary>
    public static bool IsIconDomain(string domain) => _iconDomains.Value.Contains(domain);
}
