using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Models;

/// <summary>
/// Persisted record of an alert that was triggered by a rule.
/// </summary>
public class AlertHistoryEntry
{
    public int Id { get; set; }

    /// <summary>
    /// Event type that triggered this alert.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Alert severity.
    /// </summary>
    public AlertSeverity Severity { get; set; }

    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    public AlertStatus Status { get; set; } = AlertStatus.Active;

    /// <summary>
    /// Source module (e.g., "audit", "speedtest").
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Alert title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Device identifier (MAC or IP).
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Device display name.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Device IP address.
    /// </summary>
    public string? DeviceIp { get; set; }

    /// <summary>
    /// ID of the rule that triggered this alert.
    /// </summary>
    public int? RuleId { get; set; }

    /// <summary>
    /// ID of the correlated incident, if grouped.
    /// </summary>
    public int? IncidentId { get; set; }

    /// <summary>
    /// JSON-serialized context data.
    /// </summary>
    public string? ContextJson { get; set; }

    /// <summary>
    /// When the alert was triggered.
    /// </summary>
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the alert was acknowledged.
    /// </summary>
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>
    /// When the alert was resolved.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Comma-separated channel IDs that received this alert.
    /// </summary>
    public string? DeliveredToChannels { get; set; }

    /// <summary>
    /// Whether delivery succeeded.
    /// </summary>
    public bool DeliverySucceeded { get; set; }

    /// <summary>
    /// Delivery error message if failed.
    /// </summary>
    public string? DeliveryError { get; set; }
}
