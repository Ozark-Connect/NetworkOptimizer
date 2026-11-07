using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One UniFi Network application build some site's console has been offered, pooled across all
/// sites in the main database. A console's own updateAvailable is stale until its background
/// check runs; this remembers builds other consoles on the same channel have already seen.
/// </summary>
public class SharedNetworkAppBuild
{
    /// <summary>
    /// Package platform of the offering console (<c>uos-deb13-arm64</c>), or <see cref="StandalonePlatform"/>
    /// for a UniFi OS Server. Ubiquiti offers a build per platform, so an offer only speaks for its own.
    /// Empty on rows recorded before the platform was kept: those are never adopted.
    /// </summary>
    [MaxLength(32)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>The <see cref="Platform"/> key for a UniFi OS Server, whose package platform is not read.</summary>
    public const string StandalonePlatform = "standalone";

    /// <summary>Channel the offering application follows: "release", "release-candidate", "beta".</summary>
    [MaxLength(32)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Application version, e.g. "10.6.97".</summary>
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// The .deb URL captured at plan time, when one was. Not used for installs: real package URLs
    /// come from <see cref="SharedNetworkAppPackage"/>, per platform.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>When any site first reported this build.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The most recent console read that included it.</summary>
    public DateTime LastSeenUtc { get; set; }
}
