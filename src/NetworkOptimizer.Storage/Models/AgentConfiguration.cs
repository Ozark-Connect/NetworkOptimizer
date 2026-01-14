using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Stores configuration for monitoring agents
/// </summary>
public class AgentConfiguration
{
    [Key]
    [MaxLength(100)]
    public string AgentId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string AgentName { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string DeviceUrl { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? DeviceType { get; set; }

    /// <summary>
    /// Polling interval in seconds
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Enable metrics collection
    /// </summary>
    public bool MetricsEnabled { get; set; } = true;

    /// <summary>
    /// Enable SQM monitoring
    /// </summary>
    public bool SqmEnabled { get; set; } = false;

    /// <summary>
    /// Enable audit checks
    /// </summary>
    public bool AuditEnabled { get; set; } = false;

    /// <summary>
    /// Audit interval in hours
    /// </summary>
    public int AuditIntervalHours { get; set; } = 24;

    /// <summary>
    /// InfluxDB batch size
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// InfluxDB flush interval in seconds
    /// </summary>
    public int FlushIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// JSON serialized additional settings
    /// </summary>
    public string? AdditionalSettingsJson { get; set; }

    /// <summary>
    /// Agent enabled/disabled status
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
}
