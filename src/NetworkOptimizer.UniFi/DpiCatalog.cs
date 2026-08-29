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

    /// <summary>The Font Awesome brand class for this application, if the catalog marks it with one.</summary>
    public static string? IconClass(int category, int application) =>
        _catalog.Value.Applications.TryGetValue(Key(category, application).ToString(), out var e) ? e.I : null;

    /// <summary>Whether a domain is one the catalog names, so the icon path cannot be pointed anywhere else.</summary>
    public static bool IsIconDomain(string domain) => _iconDomains.Value.Contains(domain);
}
