using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Represents an alert generated when a metric exceeds a threshold
/// </summary>
public class Alert
{
    /// <summary>
    /// Unique identifier for the alert
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Alert severity level
    /// </summary>
    public AlertSeverity Severity { get; set; }

    /// <summary>
    /// Alert status
    /// </summary>
    public AlertStatus Status { get; set; } = AlertStatus.Active;

    /// <summary>
    /// Type of metric that triggered the alert
    /// </summary>
    public string MetricType { get; set; } = string.Empty;

    /// <summary>
    /// Name of the specific metric
    /// </summary>
    public string MetricName { get; set; } = string.Empty;

    /// <summary>
    /// Current value of the metric
    /// </summary>
    public double CurrentValue { get; set; }

    /// <summary>
    /// Threshold value that was exceeded
    /// </summary>
    public double ThresholdValue { get; set; }

    /// <summary>
    /// Comparison operator used for threshold
    /// </summary>
    public ThresholdComparison Comparison { get; set; }

    /// <summary>
    /// Device IP address where alert originated
    /// </summary>
    public string DeviceIp { get; set; } = string.Empty;

    /// <summary>
    /// Device hostname
    /// </summary>
    public string DeviceHostname { get; set; } = string.Empty;

    /// <summary>
    /// Interface description (if applicable)
    /// </summary>
    public string? InterfaceDescription { get; set; }

    /// <summary>
    /// Alert title/summary
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed alert message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// When the alert was first triggered
    /// </summary>
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the alert was last updated
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// When the alert was resolved (if applicable)
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// How long the alert has been active
    /// </summary>
    public TimeSpan Duration => (ResolvedAt ?? DateTime.UtcNow) - TriggeredAt;

    /// <summary>
    /// Number of times this alert has been triggered consecutively
    /// </summary>
    public int TriggerCount { get; set; } = 1;

    /// <summary>
    /// Additional context data
    /// </summary>
    public Dictionary<string, object> Context { get; set; } = new();

    /// <summary>
    /// Tags for categorization and filtering
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Whether the alert has been acknowledged
    /// </summary>
    public bool IsAcknowledged { get; set; }

    /// <summary>
    /// User who acknowledged the alert
    /// </summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>
    /// When the alert was acknowledged
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// Notes added to the alert
    /// </summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Threshold comparison operators
/// </summary>
public enum ThresholdComparison
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
    NotEqual
}
