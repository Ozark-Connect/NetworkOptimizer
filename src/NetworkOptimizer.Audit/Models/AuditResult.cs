namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Comprehensive audit results for a UniFi network configuration
/// </summary>
public class AuditResult
{
    /// <summary>
    /// Timestamp when the audit was performed
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Client/site name
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// Network topology (networks/VLANs discovered)
    /// </summary>
    public List<NetworkInfo> Networks { get; init; } = new();

    /// <summary>
    /// Switches/gateways discovered
    /// </summary>
    public List<SwitchInfo> Switches { get; init; } = new();

    /// <summary>
    /// All audit issues found
    /// </summary>
    public List<AuditIssue> Issues { get; init; } = new();

    /// <summary>
    /// Critical issues requiring immediate attention
    /// </summary>
    public List<AuditIssue> CriticalIssues => Issues.Where(i => i.Severity == AuditSeverity.Critical).ToList();

    /// <summary>
    /// Recommended improvements
    /// </summary>
    public List<AuditIssue> RecommendedIssues => Issues.Where(i => i.Severity == AuditSeverity.Recommended).ToList();

    /// <summary>
    /// Items to investigate
    /// </summary>
    public List<AuditIssue> InvestigateIssues => Issues.Where(i => i.Severity == AuditSeverity.Investigate).ToList();

    /// <summary>
    /// Security posture score (0-100)
    /// </summary>
    public int SecurityScore { get; set; }

    /// <summary>
    /// Hardening measures already in place
    /// </summary>
    public List<string> HardeningMeasures { get; init; } = new();

    /// <summary>
    /// Summary statistics
    /// </summary>
    public AuditStatistics Statistics { get; init; } = new();

    /// <summary>
    /// Overall security posture assessment
    /// </summary>
    public SecurityPosture Posture { get; set; }
}

/// <summary>
/// Summary statistics from the audit
/// </summary>
public class AuditStatistics
{
    /// <summary>
    /// Total number of ports across all switches
    /// </summary>
    public int TotalPorts { get; set; }

    /// <summary>
    /// Number of disabled ports
    /// </summary>
    public int DisabledPorts { get; set; }

    /// <summary>
    /// Number of active/up ports
    /// </summary>
    public int ActivePorts { get; set; }

    /// <summary>
    /// Number of ports with MAC restrictions
    /// </summary>
    public int MacRestrictedPorts { get; set; }

    /// <summary>
    /// Number of ports with port security enabled
    /// </summary>
    public int PortSecurityEnabledPorts { get; set; }

    /// <summary>
    /// Number of isolated ports
    /// </summary>
    public int IsolatedPorts { get; set; }

    /// <summary>
    /// Number of unprotected active ports
    /// </summary>
    public int UnprotectedActivePorts { get; set; }

    /// <summary>
    /// Percentage of ports that are hardened (0-100)
    /// </summary>
    public double HardeningPercentage => TotalPorts > 0
        ? (double)(MacRestrictedPorts + DisabledPorts) / TotalPorts * 100
        : 0;
}

/// <summary>
/// Overall security posture assessment
/// </summary>
public enum SecurityPosture
{
    /// <summary>
    /// Excellent security posture - no critical issues
    /// </summary>
    Excellent,

    /// <summary>
    /// Good security posture - minimal issues
    /// </summary>
    Good,

    /// <summary>
    /// Fair security posture - some improvements needed
    /// </summary>
    Fair,

    /// <summary>
    /// Poor security posture - needs attention
    /// </summary>
    NeedsAttention,

    /// <summary>
    /// Critical security posture - immediate action required
    /// </summary>
    Critical
}
