using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// An individual image overlay on a floor plan, with independent positioning and styling.
/// Multiple images can be associated with a single floor (e.g. shared building floors).
/// </summary>
public class FloorPlanImage
{
    [Key]
    public int Id { get; set; }

    /// <summary>Parent floor plan</summary>
    public int FloorPlanId { get; set; }

    /// <summary>Display label (e.g. "Main Wing", "East Annex")</summary>
    [MaxLength(100)]
    public string Label { get; set; } = "";

    /// <summary>Relative path to image file within floor-plans data directory</summary>
    [MaxLength(500)]
    public string ImagePath { get; set; } = "";

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

    /// <summary>Rotation in degrees (0 - 360)</summary>
    public double RotationDeg { get; set; }

    /// <summary>JSON crop definition: { top, right, bottom, left } as percentages</summary>
    public string? CropJson { get; set; }

    /// <summary>Display order (lower = rendered first / behind)</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public FloorPlan FloorPlan { get; set; } = null!;
}
