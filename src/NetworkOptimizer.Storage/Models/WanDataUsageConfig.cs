using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

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
    /// Day of month the billing cycle starts (1-28)
    /// </summary>
    public int BillingCycleDayOfMonth { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
