using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Long-term record of a neighbor network one of our AP radios has seen: one row per
/// (AP, band, BSSID, channel). A serving radio (no dedicated scan radio) mostly hears
/// neighbors on its own channel, so once it moves it forgets what the old channel's
/// neighborhood looked like - this table remembers, letting the Channel Recommendation
/// engine keep decayed neighbor evidence for channels the radio isn't currently on.
/// A neighbor that moves channels gets a new row; the old row stops updating and ages out.
/// </summary>
public class ApNeighborSighting
{
    [Key]
    public int Id { get; set; }

    /// <summary>Observing AP MAC address (lowercase, colon-separated)</summary>
    [Required]
    [MaxLength(17)]
    public string ApMac { get; set; } = "";

    /// <summary>Radio band code - "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz)</summary>
    [Required]
    [MaxLength(10)]
    public string Band { get; set; } = "";

    /// <summary>Neighbor BSSID (lowercase, colon-separated)</summary>
    [Required]
    [MaxLength(17)]
    public string Bssid { get; set; } = "";

    /// <summary>Control channel the neighbor was seen on</summary>
    public int Channel { get; set; }

    /// <summary>Neighbor channel width in MHz; 0 when unknown</summary>
    public int WidthMhz { get; set; }

    /// <summary>Strongest signal (dBm) observed for this sighting. Deliberately the max, not
    /// the latest - matching the conservative bias of the live sighting pool, so a channel
    /// only reads clean when its remembered neighbor was consistently weak.</summary>
    public int SignalDbm { get; set; }

    /// <summary>Number of collection cycles this neighbor has been seen on this channel.
    /// Distinguishes a durable neighbor (seen cycle after cycle) from a one-off (a guest
    /// hotspot, a device passing through) so a transient sighting can't accumulate into
    /// phantom load. Scales the remembered sighting's confidence at recommendation time.</summary>
    public int SightingCount { get; set; }

    /// <summary>Neighbor SSID at last sighting (may be empty for hidden networks)</summary>
    [MaxLength(64)]
    public string? Ssid { get; set; }

    /// <summary>First time this neighbor was seen on this channel by this radio (UTC)</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Most recent sighting (UTC) - drives age decay and pruning</summary>
    public DateTime LastSeenUtc { get; set; }
}
