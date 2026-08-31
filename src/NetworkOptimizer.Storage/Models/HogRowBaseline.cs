using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A Bandwidth Hogs row's learned baseline local rate: what the device moves constantly that the
/// gateway's WAN figures never accounted for (a camera feed into an NVR). Learned live from the
/// rate and console histories and persisted so a restart starts armed instead of re-attributing
/// that traffic to the WAN while it re-learns. One row per Hogs row key (client, port, or wired
/// fallback - see MonitoringLiveStats' row keys).
/// </summary>
public class HogRowBaseline
{
    [Key]
    [MaxLength(120)]
    public string RowKey { get; set; } = string.Empty;

    /// <summary>Baseline toward the device, bits per second.</summary>
    public double DownBps { get; set; }

    /// <summary>Baseline from the device, bits per second.</summary>
    public double UpBps { get; set; }

    /// <summary>When the baseline was last computed live; stale rows age out.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
