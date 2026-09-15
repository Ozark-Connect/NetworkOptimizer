using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Compares one severity's active findings between two audit runs, for the "new findings" alerts.
/// </summary>
public static class AuditFindingDelta
{
    /// <summary>
    /// The count of active findings at a severity in each run, and the findings in the current run that
    /// the previous run did not have. Acknowledged findings and system findings count in neither run.
    /// </summary>
    public sealed record Result(int PreviousCount, int CurrentCount, IReadOnlyList<AuditIssue> NewIssues)
    {
        /// <summary>
        /// Whether the count went up. A finding that resolved while another appeared nets to no change,
        /// which is deliberately not an alert.
        /// </summary>
        public bool Increased => CurrentCount > PreviousCount;

        /// <summary>
        /// How many more findings the current run has.
        /// </summary>
        public int Added => CurrentCount - PreviousCount;
    }

    /// <summary>
    /// Compare active findings of <paramref name="severity"/> in <paramref name="previous"/> and <paramref name="current"/>.
    /// </summary>
    public static Result Compare(
        IEnumerable<AuditIssue> previous,
        IEnumerable<AuditIssue> current,
        AuditSeverity severity,
        Func<AuditIssue, bool> isAcknowledged)
    {
        bool Counts(AuditIssue i) => i.Severity == severity && i.Category != "System" && !isAcknowledged(i);

        var previousActive = previous.Where(Counts).ToList();
        var currentActive = current.Where(Counts).ToList();
        var previousKeys = previousActive.Select(AuditService.GetIssueKey).ToHashSet();
        var newIssues = currentActive.Where(i => !previousKeys.Contains(AuditService.GetIssueKey(i))).ToList();

        return new Result(previousActive.Count, currentActive.Count, newIssues);
    }

    /// <summary>
    /// Alert title: "2 new recommendations in the scheduled Security Audit (3 → 5)".
    /// </summary>
    public static string BuildTitle(string singular, string plural, Result delta) =>
        $"{delta.Added} new {(delta.Added == 1 ? singular : plural)} in the scheduled Security Audit ({delta.PreviousCount} → {delta.CurrentCount})";

    /// <summary>
    /// Alert message: the new findings by title and device, up to five, then how many more.
    /// </summary>
    public static string BuildMessage(Result delta)
    {
        var shown = delta.NewIssues.Take(5)
            .Select(i => string.IsNullOrEmpty(i.DeviceName) ? i.Title : $"{i.Title}: {i.DeviceName}")
            .ToList();
        var more = delta.NewIssues.Count - shown.Count;
        return more > 0 ? $"{string.Join("; ", shown)}; and {more} more" : string.Join("; ", shown);
    }
}
