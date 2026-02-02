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
    /// <param name="siteId">The site ID.</param>
    /// <param name="audit">The audit result to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ID of the saved audit.</returns>
    public async Task<int> SaveAuditResultAsync(int siteId, AuditResult audit, CancellationToken cancellationToken = default)
    {
        try
        {
            audit.SiteId = siteId;
            audit.CreatedAt = DateTime.UtcNow;
            _context.AuditResults.Add(audit);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Saved audit result {AuditId} for device {DeviceId} in site {SiteId}",
                audit.Id,
                audit.DeviceId,
                siteId);
            return audit.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save audit result for device {DeviceId} in site {SiteId}", audit.DeviceId, siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves an audit result by ID.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="auditId">The audit ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit result, or null if not found.</returns>
    public async Task<AuditResult?> GetAuditResultAsync(int siteId, int auditId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AuditResults
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.SiteId == siteId && a.Id == auditId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get audit result {AuditId} in site {SiteId}", auditId, siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves the most recent audit result for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest audit result, or null if none exist.</returns>
    public async Task<AuditResult?> GetLatestAuditResultAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AuditResults
                .AsNoTracking()
                .Where(a => a.SiteId == siteId)
                .OrderByDescending(a => a.AuditDate)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get latest audit result for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves audit history for a site, optionally filtered by device.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="deviceId">Optional device ID to filter by.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of audit results ordered by date descending.</returns>
    public async Task<List<AuditResult>> GetAuditHistoryAsync(
        int siteId,
        string? deviceId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.AuditResults
                .AsNoTracking()
                .Where(a => a.SiteId == siteId);

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
            _logger.LogError(ex, "Failed to get audit history for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Gets the total count of audit results for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of audit results for the site.</returns>
    public async Task<int> GetAuditCountAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AuditResults
                .Where(a => a.SiteId == siteId)
                .CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get audit count for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Deletes audit results older than the specified date for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="olderThan">Delete audits before this date.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteOldAuditsAsync(int siteId, DateTime olderThan, CancellationToken cancellationToken = default)
    {
        try
        {
            var oldAudits = await _context.AuditResults
                .Where(a => a.SiteId == siteId && a.AuditDate < olderThan)
                .ToListAsync(cancellationToken);

            if (oldAudits.Count > 0)
            {
                _context.AuditResults.RemoveRange(oldAudits);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted {Count} old audit results for site {SiteId}", oldAudits.Count, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete old audits for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Clears all audit results for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAllAuditsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            var allAudits = await _context.AuditResults
                .Where(a => a.SiteId == siteId)
                .ToListAsync(cancellationToken);
            if (allAudits.Count > 0)
            {
                _context.AuditResults.RemoveRange(allAudits);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} audit results for site {SiteId}", allAudits.Count, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear all audits for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion

    #region Dismissed Issues

    /// <summary>
    /// Retrieves all dismissed audit issues for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of dismissed issues ordered by dismissal date descending.</returns>
    public async Task<List<DismissedIssue>> GetDismissedIssuesAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DismissedIssues
                .AsNoTracking()
                .Where(d => d.SiteId == siteId)
                .OrderByDescending(d => d.DismissedAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get dismissed issues for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves a dismissed issue record for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="issue">The dismissed issue to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveDismissedIssueAsync(int siteId, DismissedIssue issue, CancellationToken cancellationToken = default)
    {
        try
        {
            issue.SiteId = siteId;
            issue.DismissedAt = DateTime.UtcNow;
            _context.DismissedIssues.Add(issue);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Dismissed issue {IssueKey} for site {SiteId}", issue.IssueKey, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save dismissed issue {IssueKey} for site {SiteId}", issue.IssueKey, siteId);
            throw;
        }
    }

    /// <summary>
    /// Deletes a dismissed issue for a site, restoring it to the active issues list.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="issueKey">The unique issue key to restore.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteDismissedIssueAsync(int siteId, string issueKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var issue = await _context.DismissedIssues
                .FirstOrDefaultAsync(d => d.SiteId == siteId && d.IssueKey == issueKey, cancellationToken);
            if (issue != null)
            {
                _context.DismissedIssues.Remove(issue);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Restored dismissed issue {IssueKey} for site {SiteId}", issueKey, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete dismissed issue {IssueKey} for site {SiteId}", issueKey, siteId);
            throw;
        }
    }

    /// <summary>
    /// Clears all dismissed issues for a site, restoring them to active status.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAllDismissedIssuesAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            var allIssues = await _context.DismissedIssues
                .Where(d => d.SiteId == siteId)
                .ToListAsync(cancellationToken);
            if (allIssues.Count > 0)
            {
                _context.DismissedIssues.RemoveRange(allIssues);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} dismissed issues for site {SiteId}", allIssues.Count, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear dismissed issues for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion
}
