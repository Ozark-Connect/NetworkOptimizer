using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.ClientIcons;

/// <summary>
/// One site's client icons at one moment, by client MAC, and its UniFi devices' images, by IP.
/// Immutable: a refresh replaces the instance. Only images on disk are listed, so a page never
/// renders one that cannot load.
/// </summary>
public sealed class ClientIconMap
{
    // Declared before Empty: static fields initialize in source order, and Empty's constructor reads them.
    private static readonly IReadOnlyDictionary<string, string> NoDevices = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, int> NoRanks = new Dictionary<string, int>();
    private static readonly IReadOnlyDictionary<string, ClientDeviceCategory> NoCategories = new Dictionary<string, ClientDeviceCategory>();

    /// <summary>No icons: the map before the first refresh, or for a site with no console.</summary>
    public static readonly ClientIconMap Empty = new("", new Dictionary<string, string>());

    private readonly string _slug;
    private readonly IReadOnlyDictionary<string, string> _keyByMac;
    private readonly IReadOnlyDictionary<string, string> _deviceUrlByIp;
    private readonly IReadOnlyDictionary<string, string> _deviceCatalogUrlByMac;
    private readonly IReadOnlyDictionary<string, int> _rankByMac;
    private readonly IReadOnlyDictionary<string, ClientDeviceCategory> _categoryByMac;

    /// <param name="slug">The site the icons are served for.</param>
    /// <param name="keyByMac">Cached icon key per lowercase MAC.</param>
    /// <param name="deviceUrlByIp">Each UniFi device's image URL, by its IP.</param>
    /// <param name="deviceCatalogUrlByMac">Each UniFi device's catalog icon URL, by its lowercase MAC.</param>
    /// <param name="rankByMac">How specific each client's icon is; see <see cref="RankFor"/>.</param>
    /// <param name="categoryByMac">What kind of device each client is; see <see cref="CategoryFor"/>.</param>
    public ClientIconMap(string slug, IReadOnlyDictionary<string, string> keyByMac,
        IReadOnlyDictionary<string, string>? deviceUrlByIp = null,
        IReadOnlyDictionary<string, string>? deviceCatalogUrlByMac = null,
        IReadOnlyDictionary<string, int>? rankByMac = null,
        IReadOnlyDictionary<string, ClientDeviceCategory>? categoryByMac = null)
    {
        _slug = slug;
        _keyByMac = keyByMac;
        _deviceUrlByIp = deviceUrlByIp ?? NoDevices;
        _deviceCatalogUrlByMac = deviceCatalogUrlByMac ?? NoDevices;
        _rankByMac = rankByMac ?? NoRanks;
        _categoryByMac = categoryByMac ?? NoCategories;
    }

    /// <summary>
    /// What kind of device a client is, as the Security Audit's detector reads it from the client's
    /// fingerprint, name, and MAC vendor. Unknown when undetected or not read yet.
    /// </summary>
    public ClientDeviceCategory CategoryFor(string? mac) =>
        mac != null && _categoryByMac.TryGetValue(NormalizeMac(mac), out var category) ? category : ClientDeviceCategory.Unknown;

    /// <summary>
    /// How specific a client's icon is, to pick one icon for several clients: 3 a custom icon, 2 a
    /// Ubiquiti product or a vendor's device, 1 a generic one (Linux, Docker, a server), 0 none.
    /// </summary>
    public int RankFor(string? mac) =>
        mac != null && _rankByMac.TryGetValue(NormalizeMac(mac), out var rank) ? rank : 0;

    /// <summary>The icon UniFi Network shows for the device's model, even where the app bundles an image.</summary>
    public string? DeviceCatalogUrlFor(string? mac) =>
        mac != null && _deviceCatalogUrlByMac.TryGetValue(NormalizeMac(mac), out var url) ? url : null;

    /// <summary>
    /// The image of the UniFi device at an address: the bundled product image, else the icon UniFi
    /// Network shows for its model. Null when the address is not a device or neither exists.
    /// </summary>
    public string? DeviceUrlFor(string? ip) =>
        ip != null && _deviceUrlByIp.TryGetValue(ip.Trim(), out var url) ? url : null;

    /// <summary>Clients with an icon.</summary>
    public int Count => _keyByMac.Count;

    /// <summary>The icon's URL for a client, or null when it has none cached.</summary>
    public string? UrlFor(string? mac) =>
        mac != null && _keyByMac.TryGetValue(NormalizeMac(mac), out var key) ? Url(_slug, key) : null;

    /// <summary>The served URL of an icon key. The site parameter picks the site whose custom icons it may be.</summary>
    public static string Url(string slug, string key) =>
        $"/api/client-icons/{key}?{SiteContextService.SiteQueryParam}={Uri.EscapeDataString(slug)}";

    /// <summary>Lowercase with colons, as the console reports a MAC.</summary>
    public static string NormalizeMac(string mac) => mac.Trim().Replace('-', ':').ToLowerInvariant();
}
