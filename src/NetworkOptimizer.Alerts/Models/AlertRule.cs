using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Models;

/// <summary>
/// User-configured rule that determines when events trigger alerts and how they're delivered.
/// </summary>
public class AlertRule
{
    public int Id { get; set; }

    /// <summary>
    /// Display name for the rule.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this rule is active.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Event type pattern to match (supports trailing wildcard, e.g., "audit.*", "device.offline").
    /// </summary>
    public string EventTypePattern { get; set; } = string.Empty;

    /// <summary>
    /// Source filter (empty = all sources).
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Minimum severity for this rule to fire.
    /// </summary>
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Warning;

    /// <summary>
    /// Minimum seconds between alerts for the same rule+device combination.
    /// </summary>
    public int CooldownSeconds { get; set; } = 300;

    /// <summary>
    /// Minutes after initial alert before escalating severity (0 = no escalation).
    /// </summary>
    public int EscalationMinutes { get; set; }

    /// <summary>
    /// Severity to escalate to after EscalationMinutes.
    /// </summary>
    public AlertSeverity EscalationSeverity { get; set; } = AlertSeverity.Critical;

    /// <summary>
    /// If true, this rule only generates digest entries, not immediate alerts.
    /// </summary>
    public bool DigestOnly { get; set; }

    /// <summary>
    /// Comma-separated device IDs/IPs to scope this rule to (empty = all devices).
    /// </summary>
    public string? TargetDevices { get; set; }

    /// <summary>
    /// Percent degradation threshold for threshold-based rules (e.g., speed regression, score drop).
    /// The event's Context["drop_percent"] must meet or exceed this value for the rule to fire.
    /// Null means no threshold check (rule fires on any matching event).
    /// </summary>
    public double? ThresholdPercent { get; set; }

    /// <summary>
    /// When this rule was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this rule was last modified.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}
