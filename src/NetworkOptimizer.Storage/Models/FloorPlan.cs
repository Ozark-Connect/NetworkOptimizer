using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A floor plan image with geo-positioning, wall definitions, and building association.
/// </summary>
public class FloorPlan
{
    [Key]
    public int Id { get; set; }

    /// <summary>Parent building</summary>
    public int BuildingId { get; set; }

    /// <summary>Floor number: -1=B1, 0=Ground, 1=First, etc.</summary>
    public int FloorNumber { get; set; }

    /// <summary>Display label (e.g. "Basement", "Ground Floor")</summary>
    [MaxLength(50)]
    public string Label { get; set; } = "";

    /// <summary>Relative path to floor plan image within data directory</summary>
    [MaxLength(500)]
    public string? ImagePath { get; set; }

    /// <summary>Southwest corner latitude of image overlay</summary>
    public double SwLatitude { get; set; }

    /// <summary>Southwest corner longitude of image overlay</summary>
    public double SwLongitude { get; set; }

    /// <summary>Northeast corner latitude of image overlay</summary>
    public double NeLatitude { get; set; }

    /// <summary>Northeast corner longitude of image overlay</summary>
    public double NeLongitude { get; set; }

    /// <summary>Image overlay opacity (0.0 - 1.0)</summary>
    public double Opacity { get; set; } = 0.7;

    /// <summary>JSON array of wall segments: [{ points: [{lat,lng}...], material: "drywall" }]</summary>
    public string? WallsJson { get; set; }

    /// <summary>Material type for this floor (e.g. "floor_wood", "floor_concrete")</summary>
    [MaxLength(50)]
    public string FloorMaterial { get; set; } = "floor_wood";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Building Building { get; set; } = null!;
}
