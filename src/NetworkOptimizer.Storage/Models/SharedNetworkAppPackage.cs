using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A UniFi Network package some console really installed, or an admin pasted: its real download
/// URL for one console platform, pooled across all sites in the main database. Early Access
/// packages are only ever seen this way, since Ubiquiti's public feed lists Official builds only
/// and the <c>/unifi/&lt;version&gt;/</c> path holds public releases only.
/// </summary>
public class SharedNetworkAppPackage
{
    /// <summary>Console package platform: Debian release and architecture, e.g. "uos-deb13-arm64".</summary>
    [MaxLength(32)]
    public string Platform { get; set; } = string.Empty;

    /// <summary>Application version, e.g. "11.0.81".</summary>
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;

    /// <summary>The package URL on fw-download.ubnt.com.</summary>
    [MaxLength(512)]
    public string Url { get; set; } = string.Empty;

    /// <summary>When any site first reported this package.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The most recent report of it.</summary>
    public DateTime LastSeenUtc { get; set; }
}
