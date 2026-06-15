using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// How a WAN's data usage cycle resets.
/// </summary>
public enum DataUsageResetMode
{
    /// <summary>Usage resets automatically on a calendar day each month (default, original behavior).</summary>
    Monthly = 0,

    /// <summary>
    /// Pay-as-you-go bucket: usage accumulates open-ended and only resets when the user
    /// manually resets it (e.g. after topping up a prepaid balance that never expires).
    /// </summary>
    Manual = 1
}

/// <summary>
/// Per-WAN interface data usage tracking configuration.
/// Tracks billing cycles and data caps for ISPs with usage limits.
/// </summary>
public class WanDataUsageConfig
{
    public int Id { get; set; }

    /// <summary>
    /// UniFi WAN key (e.g., "wan", "wan1", "wan2")
    /// </summary>
    [MaxLength(20)]
    public string WanKey { get; set; } = string.Empty;

    /// <summary>
    /// Display name (e.g., "Starlink", "T-Mobile 5G")
    /// </summary>
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether data usage tracking is enabled for this WAN
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Data cap in GB per billing cycle. 0 = tracking only (no cap alerts).
    /// </summary>
    public double DataCapGb { get; set; }

    /// <summary>
    /// Percentage of cap at which to fire a warning alert (1-100)
    /// </summary>
    public int WarningThresholdPercent { get; set; } = 80;

    /// <summary>
    /// How the usage cycle resets. Monthly (default) resets on a calendar day each month.
    /// Manual is an open-ended pay-as-you-go bucket the user resets when they top up.
    /// </summary>
    public DataUsageResetMode ResetMode { get; set; } = DataUsageResetMode.Monthly;

    /// <summary>
    /// Day of month the billing cycle starts (1-28). Only used in <see cref="DataUsageResetMode.Monthly"/> mode.
    /// </summary>
    public int BillingCycleDayOfMonth { get; set; } = 1;

    /// <summary>
    /// For <see cref="DataUsageResetMode.Manual"/> mode: when the bucket was last reset (UTC).
    /// Usage is counted from this point forward. Null until the first reset, in which case
    /// counting falls back to <see cref="CreatedAt"/>. Unused in Monthly mode.
    /// </summary>
    public DateTime? LastResetAt { get; set; }

    /// <summary>
    /// Manual usage adjustment in GB. Added to the calculated usage from snapshots.
    /// Allows users to set a starting point when enabling tracking mid-cycle,
    /// or to correct the calculated total if it's off.
    /// Automatically resets to 0 when the cycle rolls over (a billing-cycle rollover in
    /// Monthly mode, or a manual reset in Manual mode).
    /// </summary>
    public double ManualAdjustmentGb { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
