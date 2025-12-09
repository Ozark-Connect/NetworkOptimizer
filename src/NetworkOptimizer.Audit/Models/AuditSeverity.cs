namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Severity levels for audit findings
/// </summary>
public enum AuditSeverity
{
    /// <summary>
    /// Informational finding - no action required
    /// </summary>
    Info,

    /// <summary>
    /// Investigation recommended to understand the configuration
    /// </summary>
    Investigate,

    /// <summary>
    /// Recommended improvement for better security posture
    /// </summary>
    Recommended,

    /// <summary>
    /// Critical security issue requiring immediate attention
    /// </summary>
    Critical
}
