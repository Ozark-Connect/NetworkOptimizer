using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One roam, folded from every AP Agent that observed it.
///
/// Roams are discrete events with a phase breakdown rather than a time series, so they live here
/// instead of InfluxDB. The phase deltas, <see cref="AuthAlgo"/> and <see cref="AuthRssiDbm"/> come
/// from stahtd, which the agent probes but does not yet stream; they stay null until it does, and
/// the hostapd-sourced fields carry the record on their own.
/// </summary>
public class ApRoamRecord
{
    [Key]
    public int Id { get; set; }

    /// <summary>The access point's own clock when the roam landed, which is what orders roams across APs.</summary>
    public DateTime RoamedAt { get; set; }

    /// <summary>When this server read the event, kept apart from the AP clock so skew stays visible.</summary>
    public DateTime ObservedAt { get; set; }

    /// <summary>The client, on its MLD MAC when the association was one link of an MLO client.</summary>
    [Required]
    [MaxLength(20)]
    public string ClientMac { get; set; } = "";

    /// <summary>The link MAC the access point reported, which differs from <see cref="ClientMac"/> only for MLO.</summary>
    [MaxLength(20)]
    public string? LinkMac { get; set; }

    /// <summary>The access point the client left, when one was known.</summary>
    [MaxLength(20)]
    public string? FromApMac { get; set; }

    /// <summary>The BSSID the client left.</summary>
    [MaxLength(20)]
    public string? FromBssid { get; set; }

    /// <summary>The access point the client joined, when the gaining AP is one of ours.</summary>
    [MaxLength(20)]
    public string? ToApMac { get; set; }

    /// <summary>The BSSID the client joined. Present even when the gaining AP runs no agent.</summary>
    [MaxLength(20)]
    public string? ToBssid { get; set; }

    /// <summary>Band token of the joined BSSID, as the agent spells it ("2.4", "5", "6").</summary>
    [MaxLength(8)]
    public string? Band { get; set; }

    /// <summary>Channel of the joined BSSID.</summary>
    public int? Channel { get; set; }

    /// <summary>Band token of the BSSID the client left.</summary>
    [MaxLength(8)]
    public string? FromBand { get; set; }

    /// <summary>Channel of the BSSID the client left.</summary>
    public int? FromChannel { get; set; }

    /// <summary>Seconds the client held the previous association, from the roam before this one.</summary>
    public double? DwellSeconds { get; set; }

    /// <summary>RSSI at the moment of joining. stahtd only; null on a hostapd-sourced record.</summary>
    public int? AuthRssiDbm { get; set; }

    /// <summary>Authentication phase duration in milliseconds. stahtd only.</summary>
    public int? AuthDeltaMs { get; set; }

    /// <summary>Association phase duration in milliseconds. stahtd only.</summary>
    public int? AssocDeltaMs { get; set; }

    /// <summary>WPA authentication phase duration in milliseconds. stahtd only.</summary>
    public int? WpaAuthDeltaMs { get; set; }

    /// <summary>Authentication algorithm, where "ft" proves 802.11r engaged. stahtd only.</summary>
    [MaxLength(16)]
    public string? AuthAlgo { get; set; }

    /// <summary>Outcome token: "success", "soft failure", or "failure".</summary>
    [MaxLength(16)]
    public string Outcome { get; set; } = "success";

    /// <summary>How the roam was seen: an association on the gaining AP, or cross-AP gossip.</summary>
    [MaxLength(24)]
    public string Source { get; set; } = "";

    /// <summary>Every access point that reported this roam, comma separated. One roam, one row.</summary>
    [MaxLength(256)]
    public string ObservedByApMacs { get; set; } = "";

    /// <summary>How many access points reported it.</summary>
    public int ObservationCount { get; set; } = 1;

    /// <summary>
    /// True when the replay window was overwritten before this roam was read, so the preceding
    /// history has a hole. Set rather than interpolated: the previous association may be wrong.
    /// </summary>
    public bool AfterEventGap { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
