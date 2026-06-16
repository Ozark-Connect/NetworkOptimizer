using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for monitoring interfaces (ONT/modem management access routes
/// deployed to the gateway).
/// </summary>
public class MonitoringInterfaceRepository : IMonitoringInterfaceRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<MonitoringInterfaceRepository> _logger;

    public MonitoringInterfaceRepository(NetworkOptimizerDbContext context, ILogger<MonitoringInterfaceRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<MonitoringInterface>> GetMonitoringInterfacesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.MonitoringInterfaces
                .AsNoTracking()
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get monitoring interfaces");
            throw;
        }
    }

    public async Task<MonitoringInterface?> GetMonitoringInterfaceAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.MonitoringInterfaces
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get monitoring interface {Id}", id);
            throw;
        }
    }

    public async Task<MonitoringInterface?> GetByTargetIpAsync(string targetIp, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.MonitoringInterfaces
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.TargetIp == targetIp, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get monitoring interface for target {TargetIp}", targetIp);
            throw;
        }
    }

    public async Task SaveMonitoringInterfaceAsync(MonitoringInterface config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.MonitoringInterfaces
                    .FirstOrDefaultAsync(m => m.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.WanIfName = config.WanIfName;
                    existing.WanVlanId = config.WanVlanId;
                    existing.WanKey = config.WanKey;
                    existing.TargetIp = config.TargetIp;
                    existing.SubnetPrefix = config.SubnetPrefix;
                    existing.GatewayLocalIp = config.GatewayLocalIp;
                    existing.SnatEnabled = config.SnatEnabled;
                    existing.WatchdogIntervalMinutes = config.WatchdogIntervalMinutes;
                    existing.IsManuallyDeployed = config.IsManuallyDeployed;
                    existing.LastError = config.LastError;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.MonitoringInterfaces.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved monitoring interface {Name} ({TargetIp})", config.Name, config.TargetIp);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save monitoring interface {Name}", config.Name);
            throw;
        }
    }

    public async Task DeleteMonitoringInterfaceAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.MonitoringInterfaces.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.MonitoringInterfaces.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Deleted monitoring interface {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete monitoring interface {Id}", id);
            throw;
        }
    }
}
