using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Models;

/// <summary>
/// A correlated group of related alerts (e.g., multiple devices offline on the same switch).
/// </summary>
public class AlertIncident
{
    public int Id { get; set; }

    /// <summary>
    /// Incident title (e.g., "Switch X lost power").
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Highest severity among grouped alerts.
    /// </summary>
    public AlertSeverity Severity { get; set; }

    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    public AlertStatus Status { get; set; } = AlertStatus.Active;

    /// <summary>
    /// Number of alerts in this incident.
    /// </summary>
    public int AlertCount { get; set; }

    /// <summary>
    /// Key used to group related alerts (e.g., "device:192.0.2.1", "switch:192.0.2.10").
    /// </summary>
    public string CorrelationKey { get; set; } = string.Empty;

    /// <summary>
    /// When the first alert in this incident was triggered.
    /// </summary>
    public DateTime FirstTriggeredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the most recent alert was added to this incident.
    /// </summary>
    public DateTime LastTriggeredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the incident was resolved.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }
}
