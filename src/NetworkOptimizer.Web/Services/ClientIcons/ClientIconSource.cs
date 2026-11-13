using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.ClientIcons;

/// <summary>A client's icon: the cache key it is stored and served under, and where it is fetched from.</summary>
/// <param name="Key">Stable for one image; a changed image gets a new key.</param>
/// <param name="SourceUrl">The https URL the image is fetched from.</param>
/// <param name="SiteOwned">A custom icon, stored with its site; the others are Ubiquiti's catalog, shared by all sites.</param>
public sealed record ClientIconRef(string Key, string SourceUrl, bool SiteOwned);

/// <summary>
/// Picks the icon UniFi Network shows for a client, in its own order: the user's custom icon, then a
/// Ubiquiti product's icon, then the fingerprinted device's icon.
/// </summary>
[VendorSpecific("UniFi", "ai_icon assets, icon_filename, and dev_id(_override) map to Ubiquiti's icon CDNs")]
public static partial class ClientIconSource
{
    /// <summary>The rendered size fetched: sharp at 2x for the 20-40 px the app draws, small on disk.</summary>
    private const int Size = 129;

    private const string FingerprintBase = "https://static.ui.com/fingerprint/0/";
    private const string UniFiDeviceBase = "https://static.ui.com/fingerprint/ui/icons/";

    /// <summary>The only hosts an icon is fetched from. Custom icon URLs come from console data, so nothing else is trusted.</summary>
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "static.ui.com",
        "image.icon-gen.svc.ui.com",
    };

    [GeneratedRegex("^(fp-[0-9]{1,9}|ud-[0-9a-f-]{36}|ai-[A-Za-z0-9_-]{8,200})$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex GuidPattern();

    /// <summary>A custom icon's file name without its size suffix: <c>{icon_id}_rev_{REV}</c>.</summary>
    [GeneratedRegex("^([A-Za-z0-9_-]{8,200})_[0-9]+x[0-9]+\\.png$")]
    private static partial Regex AiFilePattern();

    /// <summary>The client's icon, or null when UniFi Network has none for it.</summary>
    public static ClientIconRef? Resolve(UniFiClientResponse client) =>
        FromAiIcon(client.AiIcon)
        ?? FromUniFiDevice(client.IconFilename ?? client.UnifiDeviceInfoFromUcore?.IconFilename)
        ?? FromFingerprint(client.DevIdOverride ?? client.DevId);

    /// <summary>Whether a key has the shape this class produces. Keys arrive on a URL, so nothing else reaches the disk.</summary>
    public static bool IsValidKey(string? key) => key != null && KeyPattern().IsMatch(key);

    /// <summary>The fetch URL of a catalog key, or null for a custom icon (its URL is stored with the site).</summary>
    public static string? CatalogUrl(string key) =>
        key.StartsWith("fp-", StringComparison.Ordinal) ? $"{FingerprintBase}{key[3..]}_{Size}x{Size}.png"
        : key.StartsWith("ud-", StringComparison.Ordinal) ? $"{UniFiDeviceBase}{key[3..]}_{Size}x{Size}.png"
        : null;

    /// <summary>An https URL on one of Ubiquiti's icon hosts.</summary>
    public static bool IsAllowedSource(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && AllowedHosts.Contains(uri.Host);

    private static ClientIconRef? FromAiIcon(UniFiAiIcon? icon)
    {
        var assets = icon?.Assets?.Where(a => IsAllowedSource(a.Url) && a.Width > 0).ToList();
        if (assets is not { Count: > 0 })
            return null;

        // The fetched size when it is there, else the smallest larger one, else the largest.
        var asset = assets.FirstOrDefault(a => a.Width == Size)
            ?? assets.Where(a => a.Width > Size).MinBy(a => a.Width)
            ?? assets.MaxBy(a => a.Width)!;
        var match = AiFilePattern().Match(Path.GetFileName(new Uri(asset.Url!).AbsolutePath));
        return match.Success ? new ClientIconRef("ai-" + match.Groups[1].Value, asset.Url!, SiteOwned: true) : null;
    }

    /// <summary>The key of a Ubiquiti product icon by its GUID (a client's <c>icon_filename</c>, a catalog <c>icon.id</c>).</summary>
    public static string? UniFiDeviceKey(string? iconId) =>
        string.IsNullOrEmpty(iconId) || !GuidPattern().IsMatch(iconId) ? null : "ud-" + iconId.ToLowerInvariant();

    private static ClientIconRef? FromUniFiDevice(string? iconFilename) =>
        UniFiDeviceKey(iconFilename) is { } key ? new ClientIconRef(key, CatalogUrl(key)!, SiteOwned: false) : null;

    private static ClientIconRef? FromFingerprint(int? devId)
    {
        if (devId is not > 0)
            return null;
        var key = "fp-" + devId.Value;
        return new ClientIconRef(key, CatalogUrl(key)!, SiteOwned: false);
    }
}
