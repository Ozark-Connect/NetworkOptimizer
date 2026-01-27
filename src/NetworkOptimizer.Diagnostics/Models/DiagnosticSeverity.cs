namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Severity level for diagnostic issues.
/// Uses different terminology than security audit to distinguish operational vs security concerns.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>
    /// Issue should be reviewed - may cause operational problems
    /// </summary>
    Warning,

    /// <summary>
    /// Informational - hygiene/cleanliness suggestion
    /// </summary>
    Info,

    /// <summary>
    /// Cannot determine if this is an issue - needs manual review
    /// </summary>
    Unknown
}

/// <summary>
/// Confidence level for diagnostic findings.
/// Higher confidence means the issue is more likely to be a real problem.
/// </summary>
public enum DiagnosticConfidence
{
    /// <summary>
    /// VLAN on >80% of trunks but missing from this one - likely forgotten
    /// </summary>
    High,

    /// <summary>
    /// VLAN on 50-80% of trunks - may be intentional but worth checking
    /// </summary>
    Medium,

    /// <summary>
    /// VLAN on <50% of trunks - might be intentional design
    /// </summary>
    Low
}
