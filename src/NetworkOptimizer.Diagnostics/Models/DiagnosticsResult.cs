namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Complete results from running all diagnostic analyzers.
/// </summary>
public class DiagnosticsResult
{
    /// <summary>
    /// When the diagnostics were run
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Time taken to run all diagnostics
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// VLAN trunk consistency issues
    /// </summary>
    public List<TrunkConsistencyIssue> TrunkConsistencyIssues { get; set; } = new();

    /// <summary>
    /// Port profile simplification suggestions
    /// </summary>
    public List<PortProfileSuggestion> PortProfileSuggestions { get; set; } = new();

    /// <summary>
    /// AP lock issues (mobile devices locked to APs)
    /// </summary>
    public List<ApLockIssue> ApLockIssues { get; set; } = new();

    /// <summary>
    /// Access port VLAN hygiene issues
    /// </summary>
    public List<AccessPortVlanIssue> AccessPortVlanIssues { get; set; } = new();

    /// <summary>
    /// 802.1X configuration issues on trunk/AP port profiles
    /// </summary>
    public List<PortProfile8021xIssue> PortProfile8021xIssues { get; set; } = new();

    /// <summary>
    /// Performance optimization suggestions (hardware accel, jumbo frames, flow control, cellular QoS)
    /// </summary>
    public List<PerformanceIssue> PerformanceIssues { get; set; } = new();

    /// <summary>
    /// Total number of issues found
    /// </summary>
    public int TotalIssueCount =>
        TrunkConsistencyIssues.Count +
        PortProfileSuggestions.Count +
        ApLockIssues.Count +
        AccessPortVlanIssues.Count +
        PortProfile8021xIssues.Count +
        PerformanceIssues.Count;

    /// <summary>
    /// Count of warning/recommendation-level issues
    /// </summary>
    public int WarningCount =>
        ApLockIssues.Count(i => i.Severity == ApLockSeverity.Warning) +
        AccessPortVlanIssues.Count(i => i.Severity == DiagnosticSeverity.Warning) +
        TrunkConsistencyIssues.Count(i => i.Confidence == DiagnosticConfidence.High || i.Confidence == DiagnosticConfidence.Medium) +
        PortProfileSuggestions.Count(s => s.Severity == PortProfileSuggestionSeverity.Recommendation) +
        PortProfile8021xIssues.Count + // All 802.1X issues are recommendations
        PerformanceIssues.Count(i => i.Severity == PerformanceSeverity.Recommendation);

    /// <summary>
    /// Count of info-level issues
    /// </summary>
    public int InfoCount =>
        ApLockIssues.Count(i => i.Severity == ApLockSeverity.Info) +
        AccessPortVlanIssues.Count(i => i.Severity == DiagnosticSeverity.Info) +
        TrunkConsistencyIssues.Count(i => i.Confidence == DiagnosticConfidence.Low) +
        PortProfileSuggestions.Count(s => s.Severity == PortProfileSuggestionSeverity.Info) +
        PerformanceIssues.Count(i => i.Severity == PerformanceSeverity.Info);
}
