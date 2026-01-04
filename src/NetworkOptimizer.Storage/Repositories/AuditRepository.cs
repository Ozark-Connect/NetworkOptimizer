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

    /// <summary>
    /// Saves a new audit result.
    /// </summary>
    /// <param name="audit">The audit result to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ID of the saved audit.</returns>
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

    /// <summary>
    /// Retrieves an audit result by ID.
    /// </summary>
    /// <param name="auditId">The audit ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit result, or null if not found.</returns>
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

    /// <summary>
    /// Retrieves the most recent audit result.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest audit result, or null if none exist.</returns>
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

    /// <summary>
    /// Retrieves audit history, optionally filtered by device.
    /// </summary>
    /// <param name="deviceId">Optional device ID to filter by.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audit results ordered by date descending.</returns>
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

    /// <summary>
    /// Deletes audit results older than the specified date.
    /// </summary>
    /// <param name="olderThan">Delete audits before this date.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Clears all audit results from the database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAllAuditsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allAudits = await _context.AuditResults.ToListAsync(cancellationToken);
            if (allAudits.Count > 0)
            {
                _context.AuditResults.RemoveRange(allAudits);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} audit results", allAudits.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear all audits");
            throw;
        }
    }

    #endregion

    #region Dismissed Issues

    /// <summary>
    /// Retrieves all dismissed audit issues.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of dismissed issues ordered by dismissal date descending.</returns>
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

    /// <summary>
    /// Saves a dismissed issue record.
    /// </summary>
    /// <param name="issue">The dismissed issue to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Deletes a dismissed issue, restoring it to the active issues list.
    /// </summary>
    /// <param name="issueKey">The unique issue key to restore.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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

    /// <summary>
    /// Clears all dismissed issues, restoring them to active status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
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
