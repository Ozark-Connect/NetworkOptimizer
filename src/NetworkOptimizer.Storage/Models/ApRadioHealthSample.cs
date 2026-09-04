using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One window of a radio's CCA and reset counters, kept so a wedge can be trended rather than only
/// alerted on.
///
/// This lands in SQLite because no InfluxDB measurement is per-radio and the schema is
/// additive-only: adding a radio tag to device_health would change its series key, which is exactly
/// what that rule forbids.
/// </summary>
public class ApRadioHealthSample
{
    [Key]
    public int Id { get; set; }

    /// <summary>The access point's MAC, normalized to lower-case colon form.</summary>
    [Required]
    [MaxLength(20)]
    public string ApMac { get; set; } = "";

    /// <summary>Radio interface name, e.g. "wifi0".</summary>
    [Required]
    [MaxLength(32)]
    public string Radio { get; set; } = "";

    /// <summary>Band token as the agent reported it.</summary>
    [MaxLength(8)]
    public string? Band { get; set; }

    /// <summary>Operating channel at the end of the window.</summary>
    public int Channel { get; set; }

    /// <summary>When the window closed.</summary>
    public DateTime SampleAt { get; set; }

    /// <summary>Seconds between this reading and the previous one.</summary>
    public double WindowSeconds { get; set; }

    /// <summary>Movement in cycle_cnt, the radio's free-running clock.</summary>
    public long? CycleDelta { get; set; }

    /// <summary>Movement in rx_clear_cnt, the cycles the channel was seen busy.</summary>
    public long? RxClearDelta { get; set; }

    /// <summary>Movement in tx_frame_cnt, the cycles this radio spent transmitting.</summary>
    public long? TxFrameDelta { get; set; }

    /// <summary>Movement in phy_err_cnt.</summary>
    public long? PhyErrDelta { get; set; }

    /// <summary>Cumulative pdev_resets, which climbs for hours before clients abandon the band.</summary>
    public long? PdevResets { get; set; }

    /// <summary>Movement in pdev_resets over this window.</summary>
    public long? PdevResetDelta { get; set; }

    /// <summary>RxClear over Cycle. Approaching 1 with no transmit is the wedge.</summary>
    public double? BusyRatio { get; set; }

    /// <summary>Whether this window matched the CCA wedge signature.</summary>
    public bool Wedged { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
