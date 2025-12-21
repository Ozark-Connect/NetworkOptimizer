using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for audit results and dismissed issues
/// </summary>
public class AuditRepository : IAuditRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<AuditRepository> _logger;

    public AuditRepository(NetworkOptimizerDbContext context, ILogger<AuditRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Audit Results

    public async Task<int> SaveAuditResultAsync(AuditResult audit, CancellationToken cancellationToken = default)
    {
        try
        {
            audit.CreatedAt = DateTime.UtcNow;
            _context.AuditResults.Add(audit);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Saved audit result {AuditId} for device {DeviceId}",
                audit.Id,
                audit.DeviceId);
            return audit.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save audit result for device {DeviceId}", audit.DeviceId);
            throw;
        }
    }

    public async Task<AuditResult?> GetAuditResultAsync(int auditId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AuditResults
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == auditId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get audit result {AuditId}", auditId);
            throw;
        }
    }

    public async Task<AuditResult?> GetLatestAuditResultAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AuditResults
                .AsNoTracking()
                .OrderByDescending(a => a.AuditDate)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get latest audit result");
            throw;
        }
    }

    public async Task<List<AuditResult>> GetAuditHistoryAsync(
        string? deviceId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.AuditResults.AsNoTracking();

            if (!string.IsNullOrEmpty(deviceId))
            {
                query = query.Where(a => a.DeviceId == deviceId);
            }

            return await query
                .OrderByDescending(a => a.AuditDate)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get audit history");
            throw;
        }
    }

    public async Task DeleteOldAuditsAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        try
        {
            var oldAudits = await _context.AuditResults
                .Where(a => a.AuditDate < olderThan)
                .ToListAsync(cancellationToken);

            if (oldAudits.Count > 0)
            {
                _context.AuditResults.RemoveRange(oldAudits);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted {Count} old audit results", oldAudits.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete old audits");
            throw;
        }
    }

    #endregion

    #region Dismissed Issues

    public async Task<List<DismissedIssue>> GetDismissedIssuesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DismissedIssues
                .AsNoTracking()
                .OrderByDescending(d => d.DismissedAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get dismissed issues");
            throw;
        }
    }

    public async Task SaveDismissedIssueAsync(DismissedIssue issue, CancellationToken cancellationToken = default)
    {
        try
        {
            issue.DismissedAt = DateTime.UtcNow;
            _context.DismissedIssues.Add(issue);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Dismissed issue {IssueKey}", issue.IssueKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save dismissed issue {IssueKey}", issue.IssueKey);
            throw;
        }
    }

    public async Task DeleteDismissedIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var issue = await _context.DismissedIssues
                .FirstOrDefaultAsync(d => d.IssueKey == issueKey, cancellationToken);
            if (issue != null)
            {
                _context.DismissedIssues.Remove(issue);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Restored dismissed issue {IssueKey}", issueKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete dismissed issue {IssueKey}", issueKey);
            throw;
        }
    }

    public async Task ClearAllDismissedIssuesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allIssues = await _context.DismissedIssues.ToListAsync(cancellationToken);
            if (allIssues.Count > 0)
            {
                _context.DismissedIssues.RemoveRange(allIssues);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} dismissed issues", allIssues.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear dismissed issues");
            throw;
        }
    }

    #endregion
}
