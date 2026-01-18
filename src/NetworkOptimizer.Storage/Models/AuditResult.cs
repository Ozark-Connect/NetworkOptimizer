using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Stores historical audit results for network devices
/// </summary>
public class AuditResult
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Site this audit belongs to</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    [Required]
    [MaxLength(100)]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string DeviceName { get; set; } = string.Empty;

    [Required]
    public DateTime AuditDate { get; set; } = DateTime.UtcNow;

    public int TotalChecks { get; set; }
    public int PassedChecks { get; set; }
    public int FailedChecks { get; set; }
    public int WarningChecks { get; set; }

    [MaxLength(50)]
    public string? FirmwareVersion { get; set; }

    [MaxLength(100)]
    public string? Model { get; set; }

    /// <summary>
    /// JSON serialized findings/issues
    /// </summary>
    public string? FindingsJson { get; set; }

    /// <summary>
    /// JSON serialized full report data (networks, switches, wireless clients, statistics)
    /// Used for PDF generation after page reload
    /// </summary>
    public string? ReportDataJson { get; set; }

    /// <summary>
    /// Overall compliance score (0-100)
    /// </summary>
    public double ComplianceScore { get; set; }

    [MaxLength(50)]
    public string AuditVersion { get; set; } = "1.0";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
