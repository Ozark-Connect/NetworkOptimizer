namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Represents the severity level of an audit finding.
/// </summary>
public enum AuditSeverity
{
    /// <summary>
    /// Informational finding with no action required.
    /// </summary>
    Info = 0,

    /// <summary>
    /// Low severity issue that should be addressed when convenient.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium severity issue that should be addressed soon.
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High severity issue that should be addressed promptly.
    /// </summary>
    High = 3,

    /// <summary>
    /// Critical issue that requires immediate attention.
    /// </summary>
    Critical = 4
}

/// <summary>
/// Extension methods for AuditSeverity enum.
/// </summary>
public static class AuditSeverityExtensions
{
    /// <summary>
    /// Gets the numeric score associated with the severity level.
    /// Higher scores indicate more severe issues.
    /// </summary>
    public static int GetScore(this AuditSeverity severity)
    {
        return severity switch
        {
            AuditSeverity.Info => 0,
            AuditSeverity.Low => 25,
            AuditSeverity.Medium => 50,
            AuditSeverity.High => 75,
            AuditSeverity.Critical => 100,
            _ => 0
        };
    }

    /// <summary>
    /// Gets a human-readable display name for the severity level.
    /// </summary>
    public static string GetDisplayName(this AuditSeverity severity)
    {
        return severity switch
        {
            AuditSeverity.Info => "Informational",
            AuditSeverity.Low => "Low Severity",
            AuditSeverity.Medium => "Medium Severity",
            AuditSeverity.High => "High Severity",
            AuditSeverity.Critical => "Critical",
            _ => "Unknown"
        };
    }
}
