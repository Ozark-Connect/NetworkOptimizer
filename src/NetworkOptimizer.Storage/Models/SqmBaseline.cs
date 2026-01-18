using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Stores 168-hour (7-day) SQM baseline data for network interfaces
/// </summary>
public class SqmBaseline
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Site this baseline belongs to</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    [Required]
    [MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string InterfaceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string InterfaceName { get; set; } = string.Empty;

    [Required]
    public DateTime BaselineStart { get; set; }

    [Required]
    public DateTime BaselineEnd { get; set; }

    /// <summary>
    /// Number of hours in baseline (typically 168 for 7 days)
    /// </summary>
    public int BaselineHours { get; set; } = 168;

    // Traffic Statistics
    public long AvgBytesIn { get; set; }
    public long AvgBytesOut { get; set; }
    public long PeakBytesIn { get; set; }
    public long PeakBytesOut { get; set; }
    public long MedianBytesIn { get; set; }
    public long MedianBytesOut { get; set; }

    // Latency Statistics (milliseconds)
    public double AvgLatency { get; set; }
    public double PeakLatency { get; set; }
    public double P95Latency { get; set; }
    public double P99Latency { get; set; }

    // Packet Loss Statistics
    public double AvgPacketLoss { get; set; }
    public double MaxPacketLoss { get; set; }

    // Jitter Statistics (milliseconds)
    public double AvgJitter { get; set; }
    public double MaxJitter { get; set; }

    // Utilization Statistics
    public double AvgUtilization { get; set; }
    public double PeakUtilization { get; set; }

    /// <summary>
    /// JSON serialized hourly data points
    /// </summary>
    public string? HourlyDataJson { get; set; }

    /// <summary>
    /// Recommended download bandwidth (Mbps)
    /// </summary>
    public double RecommendedDownloadMbps { get; set; }

    /// <summary>
    /// Recommended upload bandwidth (Mbps)
    /// </summary>
    public double RecommendedUploadMbps { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
