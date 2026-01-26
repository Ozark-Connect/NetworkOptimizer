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
    /// Total number of issues found
    /// </summary>
    public int TotalIssueCount =>
        TrunkConsistencyIssues.Count +
        PortProfileSuggestions.Count +
        ApLockIssues.Count +
        AccessPortVlanIssues.Count;

    /// <summary>
    /// Count of warning-level issues
    /// </summary>
    public int WarningCount =>
        ApLockIssues.Count(i => i.Severity == ApLockSeverity.Warning) +
        AccessPortVlanIssues.Count(i => i.Severity == DiagnosticSeverity.Warning) +
        TrunkConsistencyIssues.Count(i => i.Confidence == DiagnosticConfidence.High);
}
