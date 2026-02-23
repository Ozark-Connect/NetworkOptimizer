namespace NetworkOptimizer.Alerts.Models;

/// <summary>
/// A scheduled task that runs periodically (audit, WAN speed test, LAN speed test).
/// </summary>
public class ScheduledTask
{
    public int Id { get; set; }

    /// <summary>
    /// Task type: "audit", "wan_speedtest", "lan_speedtest"
    /// </summary>
    public string TaskType { get; set; } = string.Empty;

    /// <summary>
    /// Display name shown in the UI.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this schedule is active.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Interval in minutes between runs. Common values: 360 (6h), 720 (12h), 1440 (24h), 2880 (48h), 10080 (7d).
    /// </summary>
    public int FrequencyMinutes { get; set; }

    /// <summary>
    /// Optional custom morning run hour (0-23). If set with CustomMorningMinute, overrides interval for morning runs.
    /// </summary>
    public int? CustomMorningHour { get; set; }

    /// <summary>
    /// Optional custom morning run minute (0-59).
    /// </summary>
    public int? CustomMorningMinute { get; set; }

    /// <summary>
    /// Optional custom evening run hour (0-23). If set with CustomEveningMinute, overrides interval for evening runs.
    /// </summary>
    public int? CustomEveningHour { get; set; }

    /// <summary>
    /// Optional custom evening run minute (0-59).
    /// </summary>
    public int? CustomEveningMinute { get; set; }

    /// <summary>
    /// Target identifier: device host for LAN, WAN interface name for WAN, null for audit.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// JSON configuration for the task (e.g., max load, test location for WAN tests).
    /// </summary>
    public string? TargetConfig { get; set; }

    /// <summary>
    /// When this task last ran (UTC).
    /// </summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>
    /// When this task should next run (UTC).
    /// </summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>
    /// Result of last run: "success", "failed", "skipped".
    /// </summary>
    public string? LastStatus { get; set; }

    /// <summary>
    /// Error message from last failed run.
    /// </summary>
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// Summary of last result (e.g., "Score: 87" or "942/940 Mbps").
    /// </summary>
    public string? LastResultSummary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
