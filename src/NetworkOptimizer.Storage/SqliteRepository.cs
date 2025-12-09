using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage;

/// <summary>
/// SQLite repository for local configuration and audit storage
/// </summary>
public class SqliteRepository : ILocalRepository, IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<SqliteRepository> _logger;
    private bool _disposed;

    public SqliteRepository(NetworkOptimizerDbContext context, ILogger<SqliteRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Audit History

    /// <summary>
    /// Save a new audit result
    /// </summary>
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
    /// Get a specific audit result by ID
    /// </summary>
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
    /// Get audit history, optionally filtered by device
    /// </summary>
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
    /// Delete old audit results
    /// </summary>
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

    #region SQM Baselines

    /// <summary>
    /// Save a new SQM baseline
    /// </summary>
    public async Task<int> SaveSqmBaselineAsync(SqmBaseline baseline, CancellationToken cancellationToken = default)
    {
        try
        {
            baseline.CreatedAt = DateTime.UtcNow;
            baseline.UpdatedAt = DateTime.UtcNow;
            _context.SqmBaselines.Add(baseline);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Saved SQM baseline {BaselineId} for device {DeviceId} interface {InterfaceId}",
                baseline.Id,
                baseline.DeviceId,
                baseline.InterfaceId);
            return baseline.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to save SQM baseline for device {DeviceId} interface {InterfaceId}",
                baseline.DeviceId,
                baseline.InterfaceId);
            throw;
        }
    }

    /// <summary>
    /// Get SQM baseline for a specific device and interface
    /// </summary>
    public async Task<SqmBaseline?> GetSqmBaselineAsync(
        string deviceId,
        string interfaceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SqmBaselines
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.DeviceId == deviceId && b.InterfaceId == interfaceId,
                    cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to get SQM baseline for device {DeviceId} interface {InterfaceId}",
                deviceId,
                interfaceId);
            throw;
        }
    }

    /// <summary>
    /// Get all SQM baselines, optionally filtered by device
    /// </summary>
    public async Task<List<SqmBaseline>> GetAllSqmBaselinesAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.SqmBaselines.AsNoTracking();

            if (!string.IsNullOrEmpty(deviceId))
            {
                query = query.Where(b => b.DeviceId == deviceId);
            }

            return await query
                .OrderByDescending(b => b.UpdatedAt)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get SQM baselines");
            throw;
        }
    }

    /// <summary>
    /// Update an existing SQM baseline
    /// </summary>
    public async Task UpdateSqmBaselineAsync(SqmBaseline baseline, CancellationToken cancellationToken = default)
    {
        try
        {
            baseline.UpdatedAt = DateTime.UtcNow;
            _context.SqmBaselines.Update(baseline);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Updated SQM baseline {BaselineId} for device {DeviceId} interface {InterfaceId}",
                baseline.Id,
                baseline.DeviceId,
                baseline.InterfaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to update SQM baseline {BaselineId}",
                baseline.Id);
            throw;
        }
    }

    /// <summary>
    /// Delete an SQM baseline
    /// </summary>
    public async Task DeleteSqmBaselineAsync(int baselineId, CancellationToken cancellationToken = default)
    {
        try
        {
            var baseline = await _context.SqmBaselines.FindAsync([baselineId], cancellationToken);
            if (baseline != null)
            {
                _context.SqmBaselines.Remove(baseline);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted SQM baseline {BaselineId}", baselineId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete SQM baseline {BaselineId}", baselineId);
            throw;
        }
    }

    #endregion

    #region Agent Configuration

    /// <summary>
    /// Save a new agent configuration
    /// </summary>
    public async Task<int> SaveAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            config.CreatedAt = DateTime.UtcNow;
            config.UpdatedAt = DateTime.UtcNow;
            _context.AgentConfigurations.Add(config);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved agent configuration for {AgentId}", config.AgentId);
            return 1; // Return success indicator
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save agent configuration for {AgentId}", config.AgentId);
            throw;
        }
    }

    /// <summary>
    /// Get agent configuration by ID
    /// </summary>
    public async Task<AgentConfiguration?> GetAgentConfigAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AgentConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AgentId == agentId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent configuration for {AgentId}", agentId);
            throw;
        }
    }

    /// <summary>
    /// Get all agent configurations
    /// </summary>
    public async Task<List<AgentConfiguration>> GetAllAgentConfigsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AgentConfigurations
                .AsNoTracking()
                .OrderBy(a => a.AgentName)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent configurations");
            throw;
        }
    }

    /// <summary>
    /// Update an existing agent configuration
    /// </summary>
    public async Task UpdateAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            config.UpdatedAt = DateTime.UtcNow;
            _context.AgentConfigurations.Update(config);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated agent configuration for {AgentId}", config.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update agent configuration for {AgentId}", config.AgentId);
            throw;
        }
    }

    /// <summary>
    /// Delete an agent configuration
    /// </summary>
    public async Task DeleteAgentConfigAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.AgentConfigurations.FindAsync([agentId], cancellationToken);
            if (config != null)
            {
                _context.AgentConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted agent configuration for {AgentId}", agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete agent configuration for {AgentId}", agentId);
            throw;
        }
    }

    #endregion

    #region License Information

    /// <summary>
    /// Save a new license
    /// </summary>
    public async Task<int> SaveLicenseAsync(LicenseInfo license, CancellationToken cancellationToken = default)
    {
        try
        {
            license.CreatedAt = DateTime.UtcNow;
            license.UpdatedAt = DateTime.UtcNow;
            _context.Licenses.Add(license);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved license {LicenseId} for {LicensedTo}", license.Id, license.LicensedTo);
            return license.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save license");
            throw;
        }
    }

    /// <summary>
    /// Get the active license
    /// </summary>
    public async Task<LicenseInfo?> GetLicenseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Licenses
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderByDescending(l => l.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get license");
            throw;
        }
    }

    /// <summary>
    /// Update an existing license
    /// </summary>
    public async Task UpdateLicenseAsync(LicenseInfo license, CancellationToken cancellationToken = default)
    {
        try
        {
            license.UpdatedAt = DateTime.UtcNow;
            _context.Licenses.Update(license);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated license {LicenseId}", license.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update license {LicenseId}", license.Id);
            throw;
        }
    }

    #endregion

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _context?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
