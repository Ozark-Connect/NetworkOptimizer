using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One UniFi OS build some site's Cloud Gateway has been offered, pooled across all sites in the
/// main database. Ubiquiti ungates builds per console, so a build one console was offered can be
/// installed by URL on another console of the same platform before its own offer arrives.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="SharedFirmwareBuild"/> because the key differs: the console names
/// its OS builds by hardware platform ("UCGF"), while the device catalog names models by their
/// device code ("UDMA6A8"). Cloud Gateways never appear in the device catalog at all.
/// </remarks>
public class SharedUniFiOsBuild
{
    /// <summary>Hardware platform the image is for: /api/system hardware.shortname, e.g. "UCGF".</summary>
    [MaxLength(50)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>Channel the build was offered on: "release", "release-candidate", "beta".</summary>
    [MaxLength(32)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Version as the console reports it, e.g. "v6.0.11+5efda8b".</summary>
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;

    /// <summary>Direct firmware image URL - the source for an SSH <c>ubnt-systool fwupdate &lt;url&gt;</c>.</summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>Ubiquiti's publish date for the build, which the release-age gate judges.</summary>
    public DateTime? PublishedUtc { get; set; }

    /// <summary>When any site first reported this build.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The most recent console read that included it.</summary>
    public DateTime LastSeenUtc { get; set; }
}
