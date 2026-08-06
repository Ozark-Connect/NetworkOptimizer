namespace NetworkOptimizer.Alerts.Models;

/// <summary>
/// Records that a default alert rule pattern has been seeded into this database once, so
/// deleting the rule is honored across restarts. Startup only inserts a default whose pattern
/// is missing from both AlertRules and this table; without the record, every restart brought
/// back rules the user had deliberately deleted.
/// </summary>
public class SeededAlertRule
{
    public int Id { get; set; }

    /// <summary>
    /// Event type pattern of the default rule that was seeded.
    /// </summary>
    public string EventTypePattern { get; set; } = string.Empty;

    /// <summary>
    /// When the pattern was seeded, or when it was backfilled for an install whose rules
    /// predate this record.
    /// </summary>
    public DateTime SeededAt { get; set; } = DateTime.UtcNow;
}
