using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// User-placed AP geographic location for coverage map visualization.
/// Links an AP MAC address to a latitude/longitude position on the map.
/// </summary>
public class ApLocation
{
    [Key]
    public int Id { get; set; }

    /// <summary>AP MAC address (unique identifier linking to UniFi device)</summary>
    [Required]
    [MaxLength(17)]
    public string ApMac { get; set; } = "";

    /// <summary>Latitude coordinate</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude coordinate</summary>
    public double Longitude { get; set; }

    /// <summary>Floor number for multi-story buildings (future use)</summary>
    public int? Floor { get; set; }

    /// <summary>When this location was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
