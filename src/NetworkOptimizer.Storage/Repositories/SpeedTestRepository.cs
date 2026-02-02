using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for gateway SSH settings and iperf3 speed test results
/// </summary>
public class SpeedTestRepository : ISpeedTestRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<SpeedTestRepository> _logger;

    public SpeedTestRepository(NetworkOptimizerDbContext context, ILogger<SpeedTestRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Gateway SSH Settings

    /// <summary>
    /// Retrieves the gateway SSH connection settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The gateway SSH settings, or null if not configured.</returns>
    public async Task<GatewaySshSettings?> GetGatewaySshSettingsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.GatewaySshSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.SiteId == siteId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get gateway SSH settings for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates the gateway SSH connection settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="settings">The gateway SSH settings to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveGatewaySshSettingsAsync(int siteId, GatewaySshSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.GatewaySshSettings
                .FirstOrDefaultAsync(g => g.SiteId == siteId, cancellationToken);
            if (existing != null)
            {
                existing.Host = settings.Host;
                existing.Port = settings.Port;
                existing.Username = settings.Username;
                existing.Password = settings.Password;
                existing.PrivateKeyPath = settings.PrivateKeyPath;
                existing.Enabled = settings.Enabled;
                existing.Iperf3Port = settings.Iperf3Port;
                existing.TcMonitorPort = settings.TcMonitorPort;
                existing.LastTestedAt = settings.LastTestedAt;
                existing.LastTestResult = settings.LastTestResult;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                settings.SiteId = siteId;
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.GatewaySshSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved gateway SSH settings for {Host} in site {SiteId}", settings.Host, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save gateway SSH settings for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion

    #region Iperf3 Results

    /// <summary>
    /// Saves an iperf3 speed test result for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="result">The test result to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveIperf3ResultAsync(int siteId, Iperf3Result result, CancellationToken cancellationToken = default)
    {
        try
        {
            result.SiteId = siteId;
            result.TestTime = DateTime.UtcNow;
            _context.Iperf3Results.Add(result);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved iperf3 result for {DeviceHost} in site {SiteId}", result.DeviceHost, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save iperf3 result for {DeviceHost} in site {SiteId}", result.DeviceHost, siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves the most recent iperf3 test results for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="count">Maximum number of results to return (0 = no limit).</param>
    /// <param name="hours">Filter to results within the last N hours (0 = all time).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of recent results ordered by time descending.</returns>
    public async Task<List<Iperf3Result>> GetRecentIperf3ResultsAsync(int siteId, int count = 50, int hours = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.Iperf3Results
                .AsNoTracking()
                .Where(r => r.SiteId == siteId);

            // Apply date filter if specified
            if (hours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-hours);
                query = query.Where(r => r.TestTime >= cutoff);
            }

            query = query.OrderByDescending(r => r.TestTime);

            // Apply count limit if specified
            if (count > 0)
            {
                query = query.Take(count);
            }

            return await query.ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent iperf3 results for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves iperf3 test results for a specific device in a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="deviceHost">The device host to filter by.</param>
    /// <param name="count">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of results for the device ordered by time descending.</returns>
    public async Task<List<Iperf3Result>> GetIperf3ResultsForDeviceAsync(int siteId, string deviceHost, int count = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Iperf3Results
                .AsNoTracking()
                .Where(r => r.SiteId == siteId && r.DeviceHost == deviceHost)
                .OrderByDescending(r => r.TestTime)
                .Take(count)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get iperf3 results for {DeviceHost} in site {SiteId}", deviceHost, siteId);
            throw;
        }
    }

    /// <summary>
    /// Searches speed test results by device name, host, MAC, or network path involvement.
    /// </summary>
    /// <remarks>
    /// SCALABILITY NOTE: This implementation uses in-memory filtering after loading results.
    /// This is efficient for typical usage (hundreds to low thousands of results) but can be
    /// migrated to server-side SQLite JSON filtering if needed:
    ///
    /// SQLite approach (for future optimization):
    /// <code>
    /// // Filter top-level columns server-side
    /// query = query.Where(r =>
    ///     EF.Functions.Like(r.DeviceHost, $"%{filter}%") ||
    ///     EF.Functions.Like(r.DeviceName, $"%{filter}%") ||
    ///     EF.Functions.Like(r.ClientMac, $"%{filter}%"));
    ///
    /// // For JSON path filtering, use raw SQL with json_each():
    /// // SELECT * FROM Iperf3Results WHERE EXISTS (
    /// //   SELECT 1 FROM json_each(json_extract(PathAnalysisJson, '$.Path.Hops'))
    /// //   WHERE json_extract(value, '$.DeviceName') LIKE '%filter%'
    /// // )
    /// </code>
    ///
    /// Migration triggers:
    /// - Query time exceeds 500ms consistently
    /// - Users report slow search with 5000+ results
    /// - Memory pressure observed in monitoring
    /// </remarks>
    public async Task<List<Iperf3Result>> SearchIperf3ResultsAsync(int siteId, string filter, int count = 50, int hours = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return await GetRecentIperf3ResultsAsync(siteId, count, hours, cancellationToken);
            }

            var normalizedFilter = filter.Trim().ToLowerInvariant();

            var query = _context.Iperf3Results
                .AsNoTracking()
                .Where(r => r.SiteId == siteId);

            // Apply date filter server-side (always efficient)
            if (hours > 0)
            {
                var cutoff = DateTime.UtcNow.AddHours(-hours);
                query = query.Where(r => r.TestTime >= cutoff);
            }

            // FUTURE: Move top-level column filtering to server-side when scaling:
            // query = query.Where(r =>
            //     EF.Functions.Like(r.DeviceHost, $"%{normalizedFilter}%") ||
            //     EF.Functions.Like(r.DeviceName, $"%{normalizedFilter}%") ||
            //     EF.Functions.Like(r.ClientMac, $"%{normalizedFilter}%"));

            // Load results and filter in memory (PathAnalysisJson requires deserialization)
            // This is fine for typical usage - see scalability note above for migration path
            var results = await query
                .OrderByDescending(r => r.TestTime)
                .ToListAsync(cancellationToken);

            // Filter by device properties or path hops (uses shared helper)
            var filtered = results.Where(r => SpeedTestFilterHelper.MatchesFilter(r, normalizedFilter)).ToList();

            // Apply count limit after filtering
            if (count > 0)
            {
                filtered = filtered.Take(count).ToList();
            }

            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search iperf3 results for site {SiteId} with filter {Filter}", siteId, filter);
            throw;
        }
    }

    /// <summary>
    /// Deletes a single iperf3 test result by ID for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="id">The ID of the result to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the result was deleted, false if not found.</returns>
    public async Task<bool> DeleteIperf3ResultAsync(int siteId, int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _context.Iperf3Results
                .FirstOrDefaultAsync(r => r.SiteId == siteId && r.Id == id, cancellationToken);
            if (result == null)
            {
                return false;
            }

            _context.Iperf3Results.Remove(result);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Deleted iperf3 result {Id} for {DeviceHost} in site {SiteId}", id, result.DeviceHost, siteId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete iperf3 result {Id} in site {SiteId}", id, siteId);
            throw;
        }
    }

    /// <summary>
    /// Updates the notes for a speed test result.
    /// </summary>
    /// <param name="id">Result ID</param>
    /// <param name="notes">Notes text (null or empty to clear)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the result was found and updated</returns>
    public async Task<bool> UpdateIperf3ResultNotesAsync(int id, string? notes, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _context.Iperf3Results.FindAsync([id], cancellationToken);
            if (result == null)
            {
                return false;
            }

            result.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Updated notes for iperf3 result {Id}", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update notes for iperf3 result {Id}", id);
            throw;
        }
    }

    /// <summary>
    /// Clears all iperf3 test history for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearIperf3HistoryAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            var allResults = await _context.Iperf3Results
                .Where(r => r.SiteId == siteId)
                .ToListAsync(cancellationToken);
            if (allResults.Count > 0)
            {
                _context.Iperf3Results.RemoveRange(allResults);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} iperf3 results for site {SiteId}", allResults.Count, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear iperf3 history for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Gets the total count of iperf3 test results.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of results for the site.</returns>
    public async Task<int> GetIperf3ResultCountAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Iperf3Results
                .Where(r => r.SiteId == siteId)
                .CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get iperf3 result count for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion

    #region SQM WAN Configuration

    /// <summary>
    /// Retrieves an SQM WAN configuration by WAN number for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="wanNumber">The WAN number (1, 2, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The WAN configuration, or null if not found.</returns>
    public async Task<SqmWanConfiguration?> GetSqmWanConfigAsync(int siteId, int wanNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SqmWanConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.SiteId == siteId && c.WanNumber == wanNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get SQM WAN config for WAN {WanNumber} in site {SiteId}", wanNumber, siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves all SQM WAN configurations for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of WAN configurations ordered by WAN number.</returns>
    public async Task<List<SqmWanConfiguration>> GetAllSqmWanConfigsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SqmWanConfigurations
                .AsNoTracking()
                .Where(c => c.SiteId == siteId)
                .OrderBy(c => c.WanNumber)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all SQM WAN configs for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates an SQM WAN configuration for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="config">The WAN configuration to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveSqmWanConfigAsync(int siteId, SqmWanConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.SqmWanConfigurations
                .FirstOrDefaultAsync(c => c.SiteId == siteId && c.WanNumber == config.WanNumber, cancellationToken);

            if (existing != null)
            {
                existing.Enabled = config.Enabled;
                existing.ConnectionType = config.ConnectionType;
                existing.Name = config.Name;
                existing.Interface = config.Interface;
                existing.NominalDownloadMbps = config.NominalDownloadMbps;
                existing.NominalUploadMbps = config.NominalUploadMbps;
                existing.PingHost = config.PingHost;
                existing.SpeedtestServerId = config.SpeedtestServerId;
                existing.SpeedtestMorningHour = config.SpeedtestMorningHour;
                existing.SpeedtestMorningMinute = config.SpeedtestMorningMinute;
                existing.SpeedtestEveningHour = config.SpeedtestEveningHour;
                existing.SpeedtestEveningMinute = config.SpeedtestEveningMinute;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                config.SiteId = siteId;
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.SqmWanConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved SQM WAN config for WAN {WanNumber} ({Name}) in site {SiteId}", config.WanNumber, config.Name, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save SQM WAN config for WAN {WanNumber} in site {SiteId}", config.WanNumber, siteId);
            throw;
        }
    }

    /// <summary>
    /// Deletes an SQM WAN configuration by WAN number for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="wanNumber">The WAN number to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteSqmWanConfigAsync(int siteId, int wanNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.SqmWanConfigurations
                .FirstOrDefaultAsync(c => c.SiteId == siteId && c.WanNumber == wanNumber, cancellationToken);

            if (existing != null)
            {
                _context.SqmWanConfigurations.Remove(existing);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted SQM WAN config for WAN {WanNumber} in site {SiteId}", wanNumber, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete SQM WAN config for WAN {WanNumber} in site {SiteId}", wanNumber, siteId);
            throw;
        }
    }

    /// <summary>
    /// Clears all SQM WAN configurations for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ClearAllSqmWanConfigsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            var allConfigs = await _context.SqmWanConfigurations
                .Where(c => c.SiteId == siteId)
                .ToListAsync(cancellationToken);
            if (allConfigs.Count > 0)
            {
                _context.SqmWanConfigurations.RemoveRange(allConfigs);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} SQM WAN configurations for site {SiteId}", allConfigs.Count, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear SQM WAN configurations for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion
}
