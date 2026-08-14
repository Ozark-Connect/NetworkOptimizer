using System.Text.Json.Serialization;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// The slice of the console-level GET /api/system this feature needs: the console's name, whether
/// automatic backups are on, and the UniFi OS firmware block. Everything else in that (very large)
/// payload is deliberately left unmapped.
/// </summary>
[VendorSpecific("UniFi", "console-level /api/system (UniFi OS), not under /proxy/network")]
public class UniFiConsoleSystemInfo
{
    /// <summary>Product string of a self-hosted UniFi OS Server console.</summary>
    public const string StandaloneConsoleProduct = "unifi-os-server";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("autoBackupEnabled")]
    [JsonConverter(typeof(FlexibleNullableBoolConverter))]
    public bool? AutoBackupEnabled { get; set; }

    [JsonPropertyName("firmware")]
    public UniFiConsoleFirmware? Firmware { get; set; }

    [JsonPropertyName("hardware")]
    public UniFiConsoleHardware? Hardware { get; set; }

    /// <summary>
    /// Installed UniFi OS version on Cloud Gateways ("5.1.28"), comparable to the catalog build's
    /// numeric part (v5.1.28+hash). Null on consoles that do not report it. Never use
    /// ucore_version instead - its numbering scheme diverges from the catalog on every console type.
    /// </summary>
    [JsonIgnore]
    public string? InstalledOsVersion =>
        string.IsNullOrWhiteSpace(Hardware?.FirmwareVersion) ? null : Hardware.FirmwareVersion;

    /// <summary>
    /// True when this console is a self-hosted UniFi OS Server rather than a Cloud Gateway.
    /// UniFi OS updates are hard-refused on these - callers gate on this before offering one.
    /// </summary>
    [JsonIgnore]
    public bool IsStandaloneConsole =>
        EnumerateReleases().Any(r =>
            string.Equals(r.Product, StandaloneConsoleProduct, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<UniFiConsoleFirmwareRelease> EnumerateReleases()
    {
        if (Firmware?.Latest != null)
            yield return Firmware.Latest;

        if (Firmware?.LatestByChannel == null)
            yield break;

        foreach (var release in Firmware.LatestByChannel.Values)
        {
            if (release != null)
                yield return release;
        }
    }
}

/// <summary>The /api/system hardware block; only the installed-firmware fields are mapped.</summary>
[VendorSpecific("UniFi", "/api/system hardware block")]
public class UniFiConsoleHardware
{
    /// <summary>Installed UniFi OS version, catalog-comparable numeric form ("5.1.28").</summary>
    [JsonPropertyName("firmwareVersion")]
    public string? FirmwareVersion { get; set; }

    /// <summary>Hardware short name, e.g. "UCGF".</summary>
    [JsonPropertyName("shortname")]
    public string? Shortname { get; set; }
}

/// <summary>UniFi OS firmware state and the builds the console knows about.</summary>
[VendorSpecific("UniFi", "/api/system firmware block")]
public class UniFiConsoleFirmware
{
    /// <summary>The UniFi OS channel: "release", "release-candidate" or "beta".</summary>
    [JsonPropertyName("releaseChannel")]
    public string? ReleaseChannel { get; set; }

    /// <summary>UniFi OS channel options this console offers.</summary>
    [JsonPropertyName("channels")]
    public List<string> Channels { get; set; } = new();

    [JsonPropertyName("progress")]
    public UniFiConsoleFirmwareProgress? Progress { get; set; }

    [JsonPropertyName("update")]
    public UniFiConsoleFirmwareUpdate? Update { get; set; }

    /// <summary>Newest build on the console's current channel.</summary>
    [JsonPropertyName("latest")]
    public UniFiConsoleFirmwareRelease? Latest { get; set; }

    /// <summary>Newest build per channel, keyed by channel name.</summary>
    [JsonPropertyName("latestByChannel")]
    public Dictionary<string, UniFiConsoleFirmwareRelease> LatestByChannel { get; set; } = new();
}

/// <summary>Download/apply progress of a UniFi OS update ("none" when idle).</summary>
public class UniFiConsoleFirmwareProgress
{
    [JsonPropertyName("state")]
    public string? State { get; set; }
}

/// <summary>UniFi OS update state ("NOT_STARTED" when idle) and why it failed, if it did.</summary>
public class UniFiConsoleFirmwareUpdate
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("failedReason")]
    public string? FailedReason { get; set; }
}

/// <summary>
/// One UniFi OS build as the console reports it. Same shape the public release feed serves, so the
/// publish date, direct download URL and changelog link are all available without leaving the console.
/// </summary>
[VendorSpecific("UniFi", "/api/system firmware release entry")]
public class UniFiConsoleFirmwareRelease
{
    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Publish date - the input to the ripeness gate.</summary>
    [JsonPropertyName("created")]
    public DateTime? Created { get; set; }

    /// <summary>e.g. "unifi-os-server" for a self-hosted console.</summary>
    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("_links")]
    public UniFiConsoleFirmwareLinks? Links { get; set; }

    /// <summary>Direct download URL for the image.</summary>
    [JsonIgnore]
    public string? DownloadUrl => Links?.Data?.Href;

    /// <summary>Changelog URL, when the build has one published.</summary>
    [JsonIgnore]
    public string? ChangelogUrl => Links?.Upload?
        .FirstOrDefault(u => string.Equals(u.Name, "changelog", StringComparison.OrdinalIgnoreCase))?
        .Href;
}

/// <summary>HAL-style links on a firmware release entry.</summary>
public class UniFiConsoleFirmwareLinks
{
    [JsonPropertyName("data")]
    public UniFiConsoleFirmwareLink? Data { get; set; }

    [JsonPropertyName("upload")]
    public List<UniFiConsoleFirmwareLink> Upload { get; set; } = new();
}

/// <summary>A named link on a firmware release entry.</summary>
public class UniFiConsoleFirmwareLink
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("href")]
    public string? Href { get; set; }
}

/// <summary>
/// Body for PATCH /api/system/updates/channels. `applications.network` sets the UniFi Network
/// application channel; `firmware` sets the UniFi OS channel. Either or both may be sent.
/// </summary>
[VendorSpecific("UniFi", "console-level PATCH /api/system/updates/channels")]
public class UniFiConsoleUpdateChannelsRequest
{
    /// <summary>Per-application channels; only "network" is used here.</summary>
    [JsonPropertyName("applications")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Applications { get; set; }

    /// <summary>UniFi OS (console firmware) channel.</summary>
    [JsonPropertyName("firmware")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Firmware { get; set; }

    /// <summary>
    /// Composes the request from either or both channels. Returns null when neither is given -
    /// there is nothing to write, and an empty PATCH would be sent as a no-op.
    /// </summary>
    public static UniFiConsoleUpdateChannelsRequest? Build(string? networkAppChannel, string? unifiOsChannel)
    {
        var hasNetwork = !string.IsNullOrWhiteSpace(networkAppChannel);
        var hasFirmware = !string.IsNullOrWhiteSpace(unifiOsChannel);

        if (!hasNetwork && !hasFirmware)
            return null;

        return new UniFiConsoleUpdateChannelsRequest
        {
            Applications = hasNetwork
                ? new Dictionary<string, string> { ["network"] = networkAppChannel!.Trim() }
                : null,
            Firmware = hasFirmware ? unifiOsChannel!.Trim() : null
        };
    }
}
