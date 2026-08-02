using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// What we have determined about one WAN. This is the site's FIRST per-WAN table: everything of
/// this kind lives on MonitoringSettings today - AccessTechnology, WanNeighborMac, WanNeighborOui -
/// which stores per-WAN facts once per site and so cannot describe a second WAN at all. Those
/// fields belong here and should move as each of their readers is taught to ask per WAN; they are
/// deliberately not duplicated yet, so one place holds each fact.
///
/// ISP Health and Monitoring are multi-WAN planned. Keyed per WAN from the start rather than
/// widened later, so a second WAN's row is already correct when multi-WAN scoring lands, and named
/// broadly so the next per-WAN fact does not need another table.
///
/// The speeds are a cache of what the console said, never a user-editable setting: every successful
/// console read overwrites the row, and <see cref="UpdatedAt"/> is what lets a report say a figure
/// is remembered rather than current.
/// </summary>
public class WanProfile
{
    public int Id { get; set; }

    /// <summary>
    /// UniFi's WAN group - "WAN", "WAN2". The stable identity across renames and interface changes,
    /// so it is what the row is keyed on.
    /// </summary>
    [MaxLength(50)]
    public string WanNetworkgroup { get; set; } = string.Empty;

    /// <summary>Interface name as reported when the speeds were read (eth4, ppp0). Diagnostic only.</summary>
    [MaxLength(100)]
    public string? Interface { get; set; }

    /// <summary>The WAN's display name when the speeds were read. Diagnostic only.</summary>
    [MaxLength(200)]
    public string? Name { get; set; }

    /// <summary>Expected download in Mbps, null when the console reported none.</summary>
    public double? DownloadMbps { get; set; }

    /// <summary>Expected upload in Mbps, null when the console reported none.</summary>
    public double? UploadMbps { get; set; }

    /// <summary>When the console last confirmed these figures.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
