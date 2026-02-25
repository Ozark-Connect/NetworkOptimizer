namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// A detected attack pattern correlated from multiple threat events.
/// </summary>
public class ThreatPattern
{
    public int Id { get; set; }

    public PatternType PatternType { get; set; }

    /// <summary>
    /// When this pattern was detected (UTC).
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    /// JSON array of source IPs involved in this pattern.
    /// </summary>
    public string SourceIpsJson { get; set; } = "[]";

    /// <summary>
    /// Primary target port, if applicable.
    /// </summary>
    public int? TargetPort { get; set; }

    /// <summary>
    /// Number of contributing events.
    /// </summary>
    public int EventCount { get; set; }

    /// <summary>
    /// Earliest event timestamp in this pattern.
    /// </summary>
    public DateTime FirstSeen { get; set; }

    /// <summary>
    /// Latest event timestamp in this pattern.
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Confidence score 0.0-1.0.
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// When an alert was last published for this pattern. Null if never alerted.
    /// Used to prevent duplicate alerts - only alert when LastSeen > LastAlertedAt.
    /// </summary>
    public DateTime? LastAlertedAt { get; set; }

    /// <summary>
    /// Human-readable description of the detected pattern.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Events associated with this pattern.
    /// </summary>
    public List<ThreatEvent> Events { get; set; } = [];
}
