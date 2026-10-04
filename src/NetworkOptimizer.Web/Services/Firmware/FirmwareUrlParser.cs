using System.Text.RegularExpressions;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>What a hand-added firmware URL installs.</summary>
public enum FirmwareUrlKind
{
    /// <summary>Not recognized by any rule.</summary>
    Unknown,
    /// <summary>UniFi device firmware (APs, switches, UXG-class gateways).</summary>
    Device,
    /// <summary>A Cloud Gateway's UniFi OS image.</summary>
    UniFiOs,
    /// <summary>The UniFi Network application package.</summary>
    NetworkApp,
}

/// <summary>
/// One firmware URL taken apart. <see cref="Token"/> is the model or platform code the file name
/// carries; it is not always the code the console uses for the device (UXGPRO ships as UXGPROV2).
/// </summary>
/// <param name="Kind">The surface the rules matched, or Unknown.</param>
/// <param name="Token">Model or platform code from the file name; null for the Network application.</param>
/// <param name="Version">Version from the path.</param>
/// <param name="Directory">Path up to the file name, used to match the URL against known builds.</param>
/// <param name="Url">The canonical URL.</param>
public sealed record ParsedFirmwareUrl(FirmwareUrlKind Kind, string? Token, string Version, string Directory, string Url);

/// <summary>
/// Reads Ubiquiti firmware URLs pasted by an admin. Only Ubiquiti's own download hosts are accepted:
/// the URL ends up in an SSH install command on a gateway and is shared with every site.
/// </summary>
[VendorSpecific("UniFi", "fw-download.ubnt.com and dl.ui.com path layouts")]
public static class FirmwareUrlParser
{
    /// <summary>Hosts a firmware URL may point at.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts = ["fw-download.ubnt.com", "dl.ui.com"];

    // <hex>-<TOKEN>-<x.y.z[.n]>-<rest>.bin, e.g. 6a7a-UCGF-6.0.11-847f967b-....bin
    private static readonly Regex ImageFile = new(
        @"^[0-9a-f]+-(?<token>[A-Za-z0-9]+)-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)-[A-Za-z0-9-]+\.bin$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // /unifi/firmware/<MODEL>/<x.y.z.build>/<file>.bin, e.g. /unifi/firmware/U7PRO/8.8.8.20113/BZ.ipq53xx_8.8.8+20113....bin
    private static readonly Regex DeviceDownloadPath = new(
        @"^/unifi/firmware/(?<token>[A-Za-z0-9]+)/(?<version>\d+\.\d+\.\d+(?:\.\d+)?)/[A-Za-z0-9._+-]+\.bin$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // /unifi/<version>/<package>.deb
    private static readonly Regex NetworkAppPath = new(
        @"^/unifi/(?<version>\d+\.\d+\.\d+)/(unifi-native_sysvinit|unifi_sysvinit_all)\.deb$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a URL. Returns null with a reason when the URL is not one this app can install;
    /// a URL on an allowed host whose layout no rule knows comes back as <see cref="FirmwareUrlKind.Unknown"/>
    /// with its token, for the caller to match against builds it already knows.
    /// </summary>
    /// <param name="input">The URL as entered.</param>
    /// <param name="error">Why the URL was refused, when it was.</param>
    public static ParsedFirmwareUrl? Parse(string? input, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(input)
            || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Enter a full https:// download link.";
            return null;
        }

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Only Ubiquiti download links are accepted ({string.Join(", ", AllowedHosts)}).";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "The link must be a plain download path, without a query string.";
            return null;
        }

        var path = uri.AbsolutePath;
        var url = uri.GetLeftPart(UriPartial.Path);

        var app = NetworkAppPath.Match(path);
        if (app.Success)
            return new ParsedFirmwareUrl(FirmwareUrlKind.NetworkApp, null, app.Groups["version"].Value, DirectoryOf(path), url);

        // The model is a folder here, not part of the file name, so no catalog match is needed for it.
        var download = DeviceDownloadPath.Match(path);
        if (download.Success)
            return new ParsedFirmwareUrl(
                FirmwareUrlKind.Device, download.Groups["token"].Value, download.Groups["version"].Value, DirectoryOf(path), url);

        var slash = path.LastIndexOf('/');
        var file = ImageFile.Match(path[(slash + 1)..]);
        if (!file.Success)
        {
            error = "The file name does not look like a UniFi firmware image (<id>-<model>-<version>-<id>.bin).";
            return null;
        }

        var directory = DirectoryOf(path);
        var kind = directory.ToLowerInvariant() switch
        {
            "/data/unifi-firmware" => FirmwareUrlKind.Device,
            "/data/unifi-dream" => FirmwareUrlKind.UniFiOs,
            _ => FirmwareUrlKind.Unknown,
        };

        return new ParsedFirmwareUrl(kind, file.Groups["token"].Value, file.Groups["version"].Value, directory, url);
    }

    /// <summary>
    /// The model or platform token in a stored build's URL, when it is on the same path as
    /// <paramref name="directory"/>. Null for any other URL.
    /// </summary>
    public static string? TokenIn(string? url, string directory)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(DirectoryOf(uri.AbsolutePath), directory, StringComparison.OrdinalIgnoreCase)) return null;

        var path = uri.AbsolutePath;
        var match = ImageFile.Match(path[(path.LastIndexOf('/') + 1)..]);
        return match.Success ? match.Groups["token"].Value : null;
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }
}
