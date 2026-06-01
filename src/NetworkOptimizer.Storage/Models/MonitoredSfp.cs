using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkOptimizer.Storage.Models;

public enum SfpCategory
{
    Standard = 0,
    Pon = 1,
    ActiveEthernet = 2
}

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

    public SfpCategory Category { get; set; }

    public bool IsMonitoredOnt { get; set; }

    public int? LinkSpeedMbps { get; set; }

    [MaxLength(200)]
    public string? FriendlyName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public bool IsPon => Category == SfpCategory.Pon;

    [NotMapped]
    public bool IsActiveEthernet => Category == SfpCategory.ActiveEthernet;

    [NotMapped]
    public bool IsOpticalLink => Category != SfpCategory.Standard;
}
