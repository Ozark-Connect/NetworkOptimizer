using System.ComponentModel.DataAnnotations;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Storage.Models;

public enum MonitoringTargetType
{
    Fabric = 0,
    Wan = 1,
    AccessIsp = 2,
    Transit = 3,
    Custom = 4,
    InternetService = 5
}

public enum DiscoveryMethod
{
    DirectRouter = 0,
    PathProxy = 1,
    /// <summary>User manually typed in the target IP for an ASN the tracer couldn't
    /// auto-resolve. Same evidence treatment as DirectRouter (we trust the user that
    /// the IP is on the path) but rendered with a small "user-added" badge.</summary>
    UserProvided = 2,
    /// <summary>Tracer couldn't find any responding hop in the ASN and couldn't fall
    /// back to a CDN path-proxy either. No MonitoringTarget row is created for this
    /// tier; the value exists so the cloud renderer can distinguish "we tried and
    /// failed" from "we never tried".</summary>
    Unresolved = 3,
    /// <summary>Target IP discovered from the gateway's ARP/neighbor table on the WAN
    /// interface (ip neigh show). This is the L2 neighbor - typically the OLT on GPON,
    /// CMTS on DOCSIS, or BNG on PPPoE. Closest upstream device; may not appear in
    /// traceroutes if it's L2-transparent.</summary>
    L2Neighbor = 4,
    /// <summary>A curated public endpoint (typically the ISP's own speedtest server) from our
    /// per-ASN fallback list, adopted as the access target when no first-mile router answers ICMP.
    /// Not discovered on the path - resolved and ping-selected from a hardcoded map.</summary>
    ConfiguredFallback = 5
}

public class MonitoringTarget
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string TargetId { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Address { get; set; } = string.Empty;

    public ProbeMode ProbeMode { get; set; } = ProbeMode.Icmp;

    /// <summary>
    /// The probe mode this target originally answered to during tracer discovery.
    /// ProbeMode (above) is what ongoing monitoring uses and can drift during
    /// re-validation; DiscoveredProbeMode is the immutable record of "what worked
    /// when we first found this," so the re-validation diff can usefully say
    /// "AS3356 used to answer ICMP, now answers TCP/443." Null for targets that
    /// weren't created by the tracer.
    /// </summary>
    public ProbeMode? DiscoveredProbeMode { get; set; }

    public int? Port { get; set; }

    public MonitoringTargetType TargetType { get; set; }

    [MaxLength(50)]
    public string? DeviceMac { get; set; }

    public int? AsnNumber { get; set; }

    [MaxLength(200)]
    public string? AsnName { get; set; }

    [Required, MaxLength(100)]
    public string VantagePoint { get; set; } = "server";

    public int PollIntervalSeconds { get; set; } = 10;

    public int PingCount { get; set; } = 10;

    public bool Enabled { get; set; } = true;

    public bool AutoDiscovered { get; set; }

    public DiscoveryMethod? DiscoveryMethod { get; set; }

    /// <summary>
    /// The WAN this target measures, or <see cref="UnpinnedWan"/> when the probe is pinned to none
    /// - which measures the primary under failover and no single WAN under load balancing.
    /// <para>
    /// A pinned value is intent, not proof. An on-gateway agent binds its probe source, so its
    /// stamp always holds - which is why multi-WAN monitoring steers users onto one. Steering an
    /// on-LAN agent or the server takes gateway policy routing instead, and that re-routes when its
    /// WAN drops, so a failover window reads as the pinned WAN's latency when it is another's.
    /// Short of a kill switch on the policy, which costs the site its probes for the whole outage.
    /// </para>
    /// </summary>
    [MaxLength(50)]
    public string? WanInterface { get; set; }

    /// <summary>The <see cref="WanInterface"/> meaning "not pinned to a WAN" - stated, not inferred.</summary>
    public const string UnpinnedWan = "unpinned";

    /// <summary>
    /// Whether a WanInterface names no WAN. Null counts: rows predating the marker carry it, and
    /// every reader must treat the two the same.
    /// </summary>
    public static bool IsUnpinned(string? wanInterface) =>
        string.IsNullOrEmpty(wanInterface)
        || string.Equals(wanInterface, UnpinnedWan, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Multi-WAN monitoring context this target belongs to (loose reference to
    /// <see cref="WanContext.Id"/> in the same site database). Null = the
    /// implicit primary context: probed as always, no `wan` tag on its points.
    /// </summary>
    public int? WanContextId { get; set; }

    [MaxLength(255)]
    public string? PtrHostname { get; set; }

    [MaxLength(200)]
    public string? AutoLabel { get; set; }

    /// <summary>
    /// When the user dismissed the "flaky LAN target" advisory for this target (the hint shown
    /// on the LAN latency chart for non-gateway/non-AP fabric devices whose loss is usually a
    /// measurement artifact). Null = never dismissed, so the hint may still show. Set once and
    /// left; it only suppresses an advisory, so there's no un-dismiss path.
    /// </summary>
    public DateTime? LanFlakyHintDismissedAt { get; set; }

    public DateTime? LastVerified { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
