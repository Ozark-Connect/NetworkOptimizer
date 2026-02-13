using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A hypothetical AP placed on the signal map for coverage planning.
/// Participates in heatmap propagation alongside real APs.
/// </summary>
public class PlannedAp
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = "";

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public int Floor { get; set; } = 1;

    public int OrientationDeg { get; set; }

    [MaxLength(20)]
    public string MountType { get; set; } = "ceiling";

    public int? TxPowerDbm { get; set; }

    [MaxLength(20)]
    public string? AntennaMode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
