using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

public class MonitoredSfp
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string DeviceMac { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string PortName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? SfpPart { get; set; }

    [MaxLength(200)]
    public string? SfpVendor { get; set; }

    /// <summary>
    /// Whether this SFP is a Passive Optical Network module — GPON, XGS-PON, etc. Use
    /// SfpPart / SfpCompliance to discriminate the specific PON variant if needed.
    /// </summary>
    public bool IsPon { get; set; }

    public bool IsMonitoredOnt { get; set; }

    public int? LinkSpeedMbps { get; set; }

    [MaxLength(200)]
    public string? FriendlyName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
