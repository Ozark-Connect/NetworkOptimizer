using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Periodic snapshot of WAN interface byte counters from the UniFi API.
/// Used to calculate data usage over billing cycles.
/// </summary>
public class WanDataUsageSnapshot
{
    public int Id { get; set; }

    /// <summary>
    /// UniFi WAN key (e.g., "wan", "wan1", "wan2")
    /// </summary>
    [MaxLength(20)]
    public string WanKey { get; set; } = string.Empty;

    /// <summary>
    /// Cumulative bytes received (from UniFi API)
    /// </summary>
    public long RxBytes { get; set; }

    /// <summary>
    /// Cumulative bytes transmitted (from UniFi API)
    /// </summary>
    public long TxBytes { get; set; }

    /// <summary>
    /// True if a counter reset was detected (counter lower than previous snapshot).
    /// Indicates gateway reboot or counter wrap.
    /// </summary>
    public bool IsCounterReset { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
