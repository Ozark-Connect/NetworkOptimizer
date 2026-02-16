namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// A performance optimization suggestion from the PerformanceAnalyzer.
/// </summary>
public class PerformanceIssue
{
    /// <summary>
    /// Short title for the issue (e.g., "Hardware Acceleration Disabled")
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Detailed description of the issue and its impact
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Actionable recommendation for fixing the issue
    /// </summary>
    public string Recommendation { get; init; } = string.Empty;

    /// <summary>
    /// Severity level of the issue
    /// </summary>
    public PerformanceSeverity Severity { get; init; }

    /// <summary>
    /// Which UI section this issue belongs to
    /// </summary>
    public PerformanceCategory Category { get; init; }

    /// <summary>
    /// Device name (if applicable to a specific device)
    /// </summary>
    public string? DeviceName { get; init; }
}

/// <summary>
/// Severity levels for performance issues.
/// </summary>
public enum PerformanceSeverity
{
    /// <summary>
    /// Informational suggestion
    /// </summary>
    Info,

    /// <summary>
    /// Recommended action
    /// </summary>
    Recommendation
}

/// <summary>
/// Which section of the Config Optimizer a performance issue belongs to.
/// </summary>
public enum PerformanceCategory
{
    /// <summary>
    /// General performance tuning (hardware accel, jumbo frames, flow control)
    /// </summary>
    Performance,

    /// <summary>
    /// Cellular data conservation (QoS rules for 5G/LTE WANs)
    /// </summary>
    CellularDataSavings
}
