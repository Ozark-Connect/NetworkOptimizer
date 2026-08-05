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

    /// <summary>
    /// Whether this WAN held the primary role when the console last said so.
    /// <para>
    /// Primary is a ROLE - failover priority and load-balance weight decide it, and any group can
    /// hold it - so it cannot be read off the name. Everything that needs the answer away from a
    /// console reads it here: the probe-push path (which has no console call available at all) and
    /// the offline fallbacks that would otherwise guess at the conventional first group and be
    /// wrong on a WAN2-primary site. Exactly one row should carry true; the writer clears the
    /// others as it sets one.
    /// </para>
    /// <para>
    /// Null means no connected compute has ever resolved the role for this site - readers must
    /// treat that as "unknown" and fall back to their documented guess, not as "not primary".
    /// </para>
    /// </summary>
    public bool? IsPrimary { get; set; }

    /// <summary>
    /// Whether the site load balances across WANs rather than running one primary with failover.
    /// Recorded per WAN because it is read per WAN, and because it changes what unpinned probing
    /// means: on a failover-only site every unpinned probe leaves by the primary, so it measures
    /// the primary honestly; under load balancing it is spread across WANs and attributable to
    /// none of them. Null when no connected compute has said.
    /// </summary>
    public bool? SiteLoadBalances { get; set; }

    /// <summary>When the console last confirmed these figures.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
