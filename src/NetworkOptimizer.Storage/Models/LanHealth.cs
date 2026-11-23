using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One hour of one link's LAN Health evidence: incident counts by kind, counter growth at each end,
/// load, speed, mesh readings, and the hour's incidents and source coverage as JSON. Written by the
/// background roll-up; the LAN Health page and the 48-hour score read these rows, never Influx.
/// </summary>
public class LanHealthLinkHour
{
    [Key]
    public int Id { get; set; }

    /// <summary>UTC start of the hour.</summary>
    public DateTime HourStart { get; set; }

    /// <summary>The link's identity: (upstream mac, port) - (downstream mac, port), or the access port.</summary>
    [MaxLength(200)]
    public string LinkId { get; set; } = "";

    /// <summary>LinkKind as an int.</summary>
    public int Kind { get; set; }

    [MaxLength(200)]
    public string? Name { get; set; }

    public int ClientsBehind { get; set; }

    // Incidents in the hour.
    public double Drops { get; set; }
    public double DropsExplained { get; set; }
    public double Relinks { get; set; }
    public double LossBursts { get; set; }
    public double ErrorBursts { get; set; }
    public double SpeedChanges { get; set; }
    /// <summary>Seconds the link was down in unexplained drops.</summary>
    public double DownSeconds { get; set; }

    // Counter growth at each end (null: the end does not report it).
    public double? UpRxErrors { get; set; }
    public double? DownRxErrors { get; set; }
    public double? UpTxErrors { get; set; }
    public double? DownTxErrors { get; set; }
    public double? UpTxDiscards { get; set; }
    public double? DownTxDiscards { get; set; }
    public double? UpRxPackets { get; set; }
    public double? DownRxPackets { get; set; }
    public double? UpTxPackets { get; set; }
    public double? DownTxPackets { get; set; }
    public double? LateCollisions { get; set; }
    public double? StpChanges { get; set; }
    /// <summary>The upstream end is a gateway LAN port whose error counters read 0 regardless.</summary>
    public bool UpErrorsBlind { get; set; }

    // Since-boot drop counters as of the hour's last reading (null: the end keeps none).
    public double? UpLinkDownCount { get; set; }
    public double? DownLinkDownCount { get; set; }
    /// <summary>The AP Agent's carrier_changes on the AP's uplink: two per drop, plus one for the boot link-up.</summary>
    public double? DownCarrierChanges { get; set; }

    // Load.
    public double? MaxInBps { get; set; }
    public double? MaxOutBps { get; set; }
    public double? MeanInBps { get; set; }
    public double? MeanOutBps { get; set; }
    public double? BroadcastPps { get; set; }
    public double? MulticastPps { get; set; }
    public double? PacketsPps { get; set; }

    // State.
    public int? SpeedMinMbps { get; set; }
    public int? SpeedMaxMbps { get; set; }
    public bool? HalfDuplex { get; set; }
    public int? MeshSignalDbm { get; set; }
    public long? MeshTxRateKbps { get; set; }
    public int? MloMembers { get; set; }
    [MaxLength(20)]
    public string? ParentMac { get; set; }
    public double? ProbeLossPct { get; set; }
    /// <summary>Median fabric probe RTT to the devices below the link, ms.</summary>
    public double? ProbeRttMs { get; set; }
    // Mesh backhaul transmit side, from the parent's downlink_table: rises over the hour.
    public double? MeshTxRetries { get; set; }
    public double? MeshTxAttempts { get; set; }
    public double? MeshTxDropped { get; set; }
    /// <summary>Times the mesh child re-associated to its parent in the hour (association uptime reset).</summary>
    public int MeshReassociations { get; set; }
    public double? PoeWattsMin { get; set; }
    public double? PoeWattsMax { get; set; }
    public int Reboots { get; set; }

    /// <summary>The downstream AP's inform failures to the UniFi Console in the hour (AP Agent).</summary>
    public int InformFailures { get; set; }

    /// <summary>The hour's incidents (start, kind, seconds, sources, explanation), for the timeline.</summary>
    [MaxLength(16000)]
    public string? IncidentsJson { get; set; }

    /// <summary>Per source: caught, due, observing.</summary>
    [MaxLength(2000)]
    public string? CoverageJson { get; set; }

    /// <summary>Roll-up version that produced the row; older rows are recomputed.</summary>
    public int Version { get; set; }

    public DateTime ComputedAt { get; set; }
}

/// <summary>One hour of the site's LAN Health: the trailing 48-hour score at that hour, and the hour's own impairment for Site Health.</summary>
public class LanHealthSiteHour
{
    [Key]
    public int Id { get; set; }

    /// <summary>UTC start of the hour.</summary>
    public DateTime HourStart { get; set; }

    /// <summary>The site's LAN Health score over the 48 hours ending with this hour; null with no links.</summary>
    public double? Score { get; set; }

    /// <summary>The hour's own LAN impairment (0 to 1): the share of user-time the LAN cost, for Site Health.</summary>
    public double? HourImpairment { get; set; }

    public int Incidents { get; set; }

    public int Explained { get; set; }

    public int FabricLinks { get; set; }

    public int AccessPorts { get; set; }

    /// <summary>The worst link's one-line label, for Site Health's hover.</summary>
    [MaxLength(200)]
    public string? Worst { get; set; }

    public int Version { get; set; }

    public DateTime ComputedAt { get; set; }
}

/// <summary>
/// A LAN Health exclusion: ports where churn is by design (hotel rooms, a lab bench). Mute keeps
/// scoring and history and stops alerts; Exclude takes the link out of the score as well. Fabric
/// links can be muted but never excluded.
/// </summary>
public class LanHealthExclusion
{
    [Key]
    public int Id { get; set; }

    /// <summary>"profile", "port", "device", or "access-all".</summary>
    [MaxLength(20)]
    public string Scope { get; set; } = "";

    /// <summary>The port profile id, the link id, the device MAC, or "" for access-all.</summary>
    [MaxLength(200)]
    public string Key { get; set; } = "";

    /// <summary>0 mute alerts, 1 exclude from score.</summary>
    public int Level { get; set; }

    /// <summary>What the exclusion names, as it read when created.</summary>
    [MaxLength(200)]
    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    public string? CreatedBy { get; set; }
}
