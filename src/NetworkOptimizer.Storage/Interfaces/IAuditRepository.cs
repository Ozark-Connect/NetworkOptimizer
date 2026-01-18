using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for audit results and dismissed issues
/// </summary>
public interface IAuditRepository
{
    // Audit Results
    Task<int> SaveAuditResultAsync(int siteId, AuditResult audit, CancellationToken cancellationToken = default);
    Task<AuditResult?> GetAuditResultAsync(int siteId, int auditId, CancellationToken cancellationToken = default);
    Task<AuditResult?> GetLatestAuditResultAsync(int siteId, CancellationToken cancellationToken = default);
    Task<List<AuditResult>> GetAuditHistoryAsync(int siteId, string? deviceId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task DeleteOldAuditsAsync(int siteId, DateTime olderThan, CancellationToken cancellationToken = default);
    Task ClearAllAuditsAsync(int siteId, CancellationToken cancellationToken = default);

    // Dismissed Issues
    Task<List<DismissedIssue>> GetDismissedIssuesAsync(int siteId, CancellationToken cancellationToken = default);
    Task SaveDismissedIssueAsync(int siteId, DismissedIssue issue, CancellationToken cancellationToken = default);
    Task DeleteDismissedIssueAsync(int siteId, string issueKey, CancellationToken cancellationToken = default);
    Task ClearAllDismissedIssuesAsync(int siteId, CancellationToken cancellationToken = default);
}
