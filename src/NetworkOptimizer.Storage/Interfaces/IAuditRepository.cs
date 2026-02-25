using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for audit results and dismissed issues
/// </summary>
public interface IAuditRepository
{
    // Audit Results
    Task<int> SaveAuditResultAsync(AuditResult audit, CancellationToken cancellationToken = default);
    Task<AuditResult?> GetAuditResultAsync(int auditId, CancellationToken cancellationToken = default);
    Task<AuditResult?> GetLatestAuditResultAsync(CancellationToken cancellationToken = default);
    Task<List<AuditResult>> GetAuditHistoryAsync(string? deviceId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task<int> GetAuditCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetManualAuditCountAsync(CancellationToken cancellationToken = default);
    Task<int> GetScheduledAuditCountAsync(CancellationToken cancellationToken = default);
    Task DeleteOldAuditsAsync(DateTime olderThan, CancellationToken cancellationToken = default);
    Task ClearAllAuditsAsync(CancellationToken cancellationToken = default);

    // Dismissed Issues
    Task<List<DismissedIssue>> GetDismissedIssuesAsync(CancellationToken cancellationToken = default);
    Task SaveDismissedIssueAsync(DismissedIssue issue, CancellationToken cancellationToken = default);
    Task DeleteDismissedIssueAsync(string issueKey, CancellationToken cancellationToken = default);
    Task ClearAllDismissedIssuesAsync(CancellationToken cancellationToken = default);
}
