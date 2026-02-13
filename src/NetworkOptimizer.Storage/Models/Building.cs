using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A building containing one or more floor plans for RF coverage mapping.
/// </summary>
public class Building
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Center latitude for initial map positioning</summary>
    public double CenterLatitude { get; set; }

    /// <summary>Center longitude for initial map positioning</summary>
    public double CenterLongitude { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<FloorPlan> Floors { get; set; } = new();
}
