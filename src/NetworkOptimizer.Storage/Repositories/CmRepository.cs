using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for cable modem configurations
/// </summary>
public class CmRepository : ICmRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<CmRepository> _logger;

    public CmRepository(NetworkOptimizerDbContext context, ILogger<CmRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<CmConfiguration>> GetCmConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.CmConfigurations
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cable modem configurations");
            throw;
        }
    }

    public async Task<List<CmConfiguration>> GetEnabledCmConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.CmConfigurations
                .AsNoTracking()
                .Where(c => c.Enabled)
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled cable modem configurations");
            throw;
        }
    }

    public async Task<CmConfiguration?> GetCmConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.CmConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cable modem configuration {Id}", id);
            throw;
        }
    }

    public async Task SaveCmConfigurationAsync(CmConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.CmConfigurations
                    .FirstOrDefaultAsync(c => c.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.Provider = config.Provider;
                    existing.Host = config.Host;
                    existing.Port = config.Port;
                    existing.Username = config.Username;
                    existing.Password = config.Password;
                    existing.StatusPagePath = config.StatusPagePath;
                    existing.Enabled = config.Enabled;
                    existing.PollingIntervalSeconds = config.PollingIntervalSeconds;
                    existing.LastPolled = config.LastPolled;
                    existing.LastError = config.LastError;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.CmConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved cable modem configuration {Name} ({Host})", config.Name, config.Host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save cable modem configuration {Name}", config.Name);
            throw;
        }
    }

    public async Task DeleteCmConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.CmConfigurations.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.CmConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Deleted cable modem configuration {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete cable modem configuration {Id}", id);
            throw;
        }
    }
}
