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

    /// <summary>
    /// The data-path interface: the logical uplink the WAN payload rides - "eth4" plain, "eth4.100"
    /// VLAN-tagged, "ppp0" for PPPoE. What SQM deploys on, and what PPPoE detection reads.
    /// </summary>
    [MaxLength(100)]
    public string? DataPathInterface { get; set; }

    /// <summary>
    /// The interface whose counters represent this WAN's throughput. NOT the same as
    /// <see cref="DataPathInterface"/> on a VLAN-tagged WAN: the sub-interface double-counts on some
    /// kernels, so the physical port wins there, while ppp0/gre1 win over their physical port
    /// because those carry exactly the WAN payload. See NetworkUtilities.PreferredWanCounterInterface,
    /// which is what fills this - the two names are stored separately because using one for the
    /// other's job silently reports the wrong throughput.
    /// </summary>
    [MaxLength(100)]
    public string? CounterInterface { get; set; }

    /// <summary>The WAN's display name when the speeds were read. Diagnostic only.</summary>
    [MaxLength(200)]
    public string? Name { get; set; }

    /// <summary>
    /// The gateway whose port counters serve this WAN, as the throughput series are tagged. Cached
    /// with the interface names because the pair is useless apart: querying stored WAN rates needs
    /// both, and both otherwise come from a console read.
    /// </summary>
    [MaxLength(50)]
    public string? GatewayMac { get; set; }

    /// <summary>Expected download in Mbps, null when the console reported none.</summary>
    public double? DownloadMbps { get; set; }

    /// <summary>Expected upload in Mbps, null when the console reported none.</summary>
    public double? UploadMbps { get; set; }

    /// <summary>When the console last confirmed these figures.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
