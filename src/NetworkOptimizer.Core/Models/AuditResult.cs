using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Represents the result of a network configuration audit.
/// </summary>
public class AuditResult
{
    /// <summary>
    /// Unique identifier for the audit result.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Timestamp when the audit was performed.
    /// </summary>
    public DateTime AuditTimestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Device identifier that was audited (if applicable).
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Device name that was audited (if applicable).
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Category of the audit finding (e.g., "Security", "Performance", "Configuration").
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Severity level of the finding.
    /// </summary>
    public AuditSeverity Severity { get; set; } = AuditSeverity.Info;

    /// <summary>
    /// Title or summary of the audit finding.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the audit finding.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Recommended action to remediate the finding.
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;

    /// <summary>
    /// Current configuration value that triggered the finding.
    /// </summary>
    public string? CurrentValue { get; set; }

    /// <summary>
    /// Expected or recommended configuration value.
    /// </summary>
    public string? ExpectedValue { get; set; }

    /// <summary>
    /// Impact score of the finding (0-100).
    /// Higher scores indicate more significant impact.
    /// </summary>
    public int ImpactScore { get; set; }

    /// <summary>
    /// Indicates whether the finding has been resolved.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>
    /// Timestamp when the finding was resolved (if applicable).
    /// </summary>
    public DateTime? ResolvedTimestamp { get; set; }

    /// <summary>
    /// Notes about the resolution or remediation.
    /// </summary>
    public string? ResolutionNotes { get; set; }

    /// <summary>
    /// Configuration path or location where the issue was found.
    /// </summary>
    public string ConfigurationPath { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata for the audit finding.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Related audit result identifiers for grouped findings.
    /// </summary>
    public List<string> RelatedFindings { get; set; } = new();

    /// <summary>
    /// Gets the calculated risk score based on severity and impact.
    /// </summary>
    public int RiskScore => (Severity.GetScore() + ImpactScore) / 2;
}

/// <summary>
/// Represents a complete audit report for a network or site.
/// </summary>
public class AuditReport
{
    /// <summary>
    /// Unique identifier for the audit report.
    /// </summary>
    public string ReportId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Timestamp when the audit was started.
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the audit was completed.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Site identifier that was audited.
    /// </summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Site name that was audited.
    /// </summary>
    public string SiteName { get; set; } = string.Empty;

    /// <summary>
    /// List of all audit findings.
    /// </summary>
    public List<AuditResult> Findings { get; set; } = new();

    /// <summary>
    /// Overall health score (0-100).
    /// Higher scores indicate better network health.
    /// </summary>
    public int OverallHealthScore { get; set; }

    /// <summary>
    /// Security score (0-100).
    /// </summary>
    public int SecurityScore { get; set; }

    /// <summary>
    /// Performance score (0-100).
    /// </summary>
    public int PerformanceScore { get; set; }

    /// <summary>
    /// Configuration compliance score (0-100).
    /// </summary>
    public int ComplianceScore { get; set; }

    /// <summary>
    /// Summary statistics for the audit.
    /// </summary>
    public AuditStatistics Statistics { get; set; } = new();

    /// <summary>
    /// Gets the duration of the audit.
    /// </summary>
    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
}

/// <summary>
/// Statistical summary of audit findings.
/// </summary>
public class AuditStatistics
{
    /// <summary>
    /// Total number of findings.
    /// </summary>
    public int TotalFindings { get; set; }

    /// <summary>
    /// Number of critical findings.
    /// </summary>
    public int CriticalCount { get; set; }

    /// <summary>
    /// Number of high severity findings.
    /// </summary>
    public int HighCount { get; set; }

    /// <summary>
    /// Number of medium severity findings.
    /// </summary>
    public int MediumCount { get; set; }

    /// <summary>
    /// Number of low severity findings.
    /// </summary>
    public int LowCount { get; set; }

    /// <summary>
    /// Number of informational findings.
    /// </summary>
    public int InfoCount { get; set; }

    /// <summary>
    /// Number of resolved findings.
    /// </summary>
    public int ResolvedCount { get; set; }

    /// <summary>
    /// Number of unresolved findings.
    /// </summary>
    public int UnresolvedCount { get; set; }

    /// <summary>
    /// Average risk score across all findings.
    /// </summary>
    public double AverageRiskScore { get; set; }
}
