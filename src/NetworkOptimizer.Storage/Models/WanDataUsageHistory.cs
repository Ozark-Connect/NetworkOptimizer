using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Durable per-WAN, per-billing-cycle usage total for a completed cycle.
/// Unlike <see cref="WanDataUsageSnapshot"/> (raw counters, pruned after ~2 months),
/// these rows are written once a billing cycle closes and kept indefinitely, giving a
/// long-term monthly history that survives snapshot pruning. One row per (WanKey, CycleStart).
/// </summary>
public class WanDataUsageHistory
{
    public int Id { get; set; }

    /// <summary>UniFi WAN network group (e.g., "WAN", "WAN2"). Matches WanDataUsageConfig.WanKey.</summary>
    [MaxLength(20)]
    public string WanKey { get; set; } = string.Empty;

    /// <summary>
    /// Display name captured at record time (denormalized so history survives a config
    /// rename or deletion).
    /// </summary>
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Start of the billing cycle this row totals (UTC), inclusive.</summary>
    public DateTime CycleStart { get; set; }

    /// <summary>End of the billing cycle this row totals (UTC) - the cycle's last day.</summary>
    public DateTime CycleEnd { get; set; }

    /// <summary>Measured data used during the cycle, in GB (does not include manual adjustment).</summary>
    public double UsedGb { get; set; }

    /// <summary>Data cap in effect for the cycle, in GB. 0 = tracking only (no cap).</summary>
    public double CapGb { get; set; }

    /// <summary>When this history row was computed and persisted (UTC).</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
