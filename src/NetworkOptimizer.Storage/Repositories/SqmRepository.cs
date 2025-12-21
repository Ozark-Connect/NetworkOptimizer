using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for SQM baseline configurations
/// </summary>
public class SqmRepository : ISqmRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<SqmRepository> _logger;

    public SqmRepository(NetworkOptimizerDbContext context, ILogger<SqmRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
}
