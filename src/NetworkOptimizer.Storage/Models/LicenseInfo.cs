using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Stores license information for the application
/// </summary>
public class LicenseInfo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required]
    [MaxLength(500)]
    public string LicenseKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string LicensedTo { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Organization { get; set; }

    [MaxLength(100)]
    public string LicenseType { get; set; } = "Free";

    /// <summary>
    /// Maximum number of devices allowed
    /// </summary>
    public int MaxDevices { get; set; } = 1;

    /// <summary>
    /// Maximum number of agents allowed
    /// </summary>
    public int MaxAgents { get; set; } = 1;

    public DateTime IssueDate { get; set; } = DateTime.UtcNow;

    public DateTime? ExpirationDate { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Features enabled by this license (JSON serialized)
    /// </summary>
    public string? FeaturesJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
