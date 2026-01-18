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

    /// <summary>
    /// Saves a new SQM baseline measurement for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="baseline">The baseline to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ID of the saved baseline.</returns>
    public async Task<int> SaveSqmBaselineAsync(int siteId, SqmBaseline baseline, CancellationToken cancellationToken = default)
    {
        try
        {
            baseline.SiteId = siteId;
            baseline.CreatedAt = DateTime.UtcNow;
            baseline.UpdatedAt = DateTime.UtcNow;
            _context.SqmBaselines.Add(baseline);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Saved SQM baseline {BaselineId} for device {DeviceId} interface {InterfaceId} in site {SiteId}",
                baseline.Id,
                baseline.DeviceId,
                baseline.InterfaceId,
                siteId);
            return baseline.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to save SQM baseline for device {DeviceId} interface {InterfaceId} in site {SiteId}",
                baseline.DeviceId,
                baseline.InterfaceId,
                siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves an SQM baseline for a specific device and interface in a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="deviceId">The device identifier.</param>
    /// <param name="interfaceId">The interface identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The baseline, or null if not found.</returns>
    public async Task<SqmBaseline?> GetSqmBaselineAsync(
        int siteId,
        string deviceId,
        string interfaceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SqmBaselines
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.SiteId == siteId && b.DeviceId == deviceId && b.InterfaceId == interfaceId,
                    cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to get SQM baseline for device {DeviceId} interface {InterfaceId} in site {SiteId}",
                deviceId,
                interfaceId,
                siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves all SQM baselines for a site, optionally filtered by device.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="deviceId">Optional device ID to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of baselines ordered by update time descending.</returns>
    public async Task<List<SqmBaseline>> GetAllSqmBaselinesAsync(
        int siteId,
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.SqmBaselines
                .AsNoTracking()
                .Where(b => b.SiteId == siteId);

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
            _logger.LogError(ex, "Failed to get SQM baselines for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Updates an existing SQM baseline in a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="baseline">The baseline with updated values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateSqmBaselineAsync(int siteId, SqmBaseline baseline, CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify baseline belongs to site
            var existing = await _context.SqmBaselines
                .FirstOrDefaultAsync(b => b.SiteId == siteId && b.Id == baseline.Id, cancellationToken);

            if (existing == null)
            {
                _logger.LogWarning("SQM baseline {BaselineId} not found in site {SiteId}", baseline.Id, siteId);
                return;
            }

            existing.DeviceId = baseline.DeviceId;
            existing.InterfaceId = baseline.InterfaceId;
            existing.InterfaceName = baseline.InterfaceName;
            existing.BaselineStart = baseline.BaselineStart;
            existing.BaselineEnd = baseline.BaselineEnd;
            existing.BaselineHours = baseline.BaselineHours;
            existing.AvgBytesIn = baseline.AvgBytesIn;
            existing.AvgBytesOut = baseline.AvgBytesOut;
            existing.PeakBytesIn = baseline.PeakBytesIn;
            existing.PeakBytesOut = baseline.PeakBytesOut;
            existing.MedianBytesIn = baseline.MedianBytesIn;
            existing.MedianBytesOut = baseline.MedianBytesOut;
            existing.AvgUtilization = baseline.AvgUtilization;
            existing.PeakUtilization = baseline.PeakUtilization;
            existing.AvgLatency = baseline.AvgLatency;
            existing.PeakLatency = baseline.PeakLatency;
            existing.P95Latency = baseline.P95Latency;
            existing.P99Latency = baseline.P99Latency;
            existing.AvgJitter = baseline.AvgJitter;
            existing.MaxJitter = baseline.MaxJitter;
            existing.AvgPacketLoss = baseline.AvgPacketLoss;
            existing.MaxPacketLoss = baseline.MaxPacketLoss;
            existing.RecommendedDownloadMbps = baseline.RecommendedDownloadMbps;
            existing.RecommendedUploadMbps = baseline.RecommendedUploadMbps;
            existing.HourlyDataJson = baseline.HourlyDataJson;
            existing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Updated SQM baseline {BaselineId} for device {DeviceId} interface {InterfaceId} in site {SiteId}",
                baseline.Id,
                baseline.DeviceId,
                baseline.InterfaceId,
                siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to update SQM baseline {BaselineId} in site {SiteId}",
                baseline.Id,
                siteId);
            throw;
        }
    }

    /// <summary>
    /// Deletes an SQM baseline by ID in a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="baselineId">The baseline ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteSqmBaselineAsync(int siteId, int baselineId, CancellationToken cancellationToken = default)
    {
        try
        {
            var baseline = await _context.SqmBaselines
                .FirstOrDefaultAsync(b => b.SiteId == siteId && b.Id == baselineId, cancellationToken);
            if (baseline != null)
            {
                _context.SqmBaselines.Remove(baseline);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted SQM baseline {BaselineId} from site {SiteId}", baselineId, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete SQM baseline {BaselineId} from site {SiteId}", baselineId, siteId);
            throw;
        }
    }
}
