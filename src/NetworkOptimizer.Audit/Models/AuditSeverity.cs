namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Severity levels for audit findings
/// </summary>
public enum AuditSeverity
{
    /// <summary>
    /// Informational finding - worth knowing but no immediate action required
    /// </summary>
    Informational,

    /// <summary>
    /// Recommended improvement for better security posture
    /// </summary>
    Recommended,

    /// <summary>
    /// Critical security issue requiring immediate attention
    /// </summary>
    Critical
}
