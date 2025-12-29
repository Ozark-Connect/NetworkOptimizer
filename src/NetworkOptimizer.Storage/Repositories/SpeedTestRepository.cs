using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    public async Task<GatewaySshSettings?> GetGatewaySshSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.GatewaySshSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get gateway SSH settings");
            throw;
        }
    }

    public async Task SaveGatewaySshSettingsAsync(GatewaySshSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.GatewaySshSettings.FirstOrDefaultAsync(cancellationToken);
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
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.GatewaySshSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved gateway SSH settings for {Host}", settings.Host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save gateway SSH settings");
            throw;
        }
    }

    #endregion

    #region Iperf3 Results

    public async Task SaveIperf3ResultAsync(Iperf3Result result, CancellationToken cancellationToken = default)
    {
        try
        {
            result.TestTime = DateTime.UtcNow;
            _context.Iperf3Results.Add(result);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved iperf3 result for {DeviceHost}", result.DeviceHost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save iperf3 result for {DeviceHost}", result.DeviceHost);
            throw;
        }
    }

    public async Task<List<Iperf3Result>> GetRecentIperf3ResultsAsync(int count = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Iperf3Results
                .AsNoTracking()
                .OrderByDescending(r => r.TestTime)
                .Take(count)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent iperf3 results");
            throw;
        }
    }

    public async Task<List<Iperf3Result>> GetIperf3ResultsForDeviceAsync(string deviceHost, int count = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Iperf3Results
                .AsNoTracking()
                .Where(r => r.DeviceHost == deviceHost)
                .OrderByDescending(r => r.TestTime)
                .Take(count)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get iperf3 results for {DeviceHost}", deviceHost);
            throw;
        }
    }

    public async Task ClearIperf3HistoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var allResults = await _context.Iperf3Results.ToListAsync(cancellationToken);
            if (allResults.Count > 0)
            {
                _context.Iperf3Results.RemoveRange(allResults);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleared {Count} iperf3 results", allResults.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear iperf3 history");
            throw;
        }
    }

    #endregion

    #region SQM WAN Configuration

    public async Task<SqmWanConfiguration?> GetSqmWanConfigAsync(int wanNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SqmWanConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.WanNumber == wanNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get SQM WAN config for WAN {WanNumber}", wanNumber);
            throw;
        }
    }

    public async Task<List<SqmWanConfiguration>> GetAllSqmWanConfigsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SqmWanConfigurations
                .AsNoTracking()
                .OrderBy(c => c.WanNumber)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all SQM WAN configs");
            throw;
        }
    }

    public async Task SaveSqmWanConfigAsync(SqmWanConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.SqmWanConfigurations
                .FirstOrDefaultAsync(c => c.WanNumber == config.WanNumber, cancellationToken);

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
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.SqmWanConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved SQM WAN config for WAN {WanNumber} ({Name})", config.WanNumber, config.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save SQM WAN config for WAN {WanNumber}", config.WanNumber);
            throw;
        }
    }

    #endregion
}
