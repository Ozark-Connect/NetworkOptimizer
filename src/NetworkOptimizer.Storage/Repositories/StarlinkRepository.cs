using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for Starlink terminal configurations
/// </summary>
public class StarlinkRepository : IStarlinkRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<StarlinkRepository> _logger;

    public StarlinkRepository(NetworkOptimizerDbContext context, ILogger<StarlinkRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<StarlinkConfiguration>> GetStarlinkConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.StarlinkConfigurations
                .AsNoTracking()
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Starlink configurations");
            throw;
        }
    }

    public async Task<List<StarlinkConfiguration>> GetEnabledStarlinkConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.StarlinkConfigurations
                .AsNoTracking()
                .Where(c => c.Enabled)
                .OrderBy(c => c.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled Starlink configurations");
            throw;
        }
    }

    public async Task<StarlinkConfiguration?> GetStarlinkConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.StarlinkConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Starlink configuration {Id}", id);
            throw;
        }
    }

    public async Task SaveStarlinkConfigurationAsync(StarlinkConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.StarlinkConfigurations
                    .FirstOrDefaultAsync(c => c.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.Provider = config.Provider;
                    existing.Host = config.Host;
                    existing.Port = config.Port;
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
                _context.StarlinkConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved Starlink configuration {Name} ({Host})", config.Name, config.Host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Starlink configuration {Name}", config.Name);
            throw;
        }
    }

    public async Task SetStarlinkEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.StarlinkConfigurations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (config == null)
                return;

            config.Enabled = enabled;
            config.UpdatedAt = DateTime.UtcNow;
            if (!enabled)
                config.LastError = null;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Set Starlink configuration {Id} Enabled={Enabled}", id, enabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set Enabled on Starlink configuration {Id}", id);
            throw;
        }
    }

    public async Task<bool> UpdateStarlinkPollResultAsync(int id, DateTime? lastPolled, string? lastError, CancellationToken cancellationToken = default)
    {
        try
        {
            // Never resurrect or overwrite a config that was disabled while the poll was
            // in flight - its frozen state (and cleared LastError) must stand. Check and
            // write in one conditional statement so a Disable committing in between
            // cannot slip through.
            var now = DateTime.UtcNow;
            int rows;
            if (lastPolled.HasValue)
                rows = await _context.StarlinkConfigurations.Where(c => c.Id == id && c.Enabled)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.LastPolled, lastPolled.Value)
                        .SetProperty(c => c.LastError, lastError)
                        .SetProperty(c => c.UpdatedAt, now), cancellationToken);
            else
                rows = await _context.StarlinkConfigurations.Where(c => c.Id == id && c.Enabled)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.LastError, lastError)
                        .SetProperty(c => c.UpdatedAt, now), cancellationToken);
            return rows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Starlink poll result for {Id}", id);
            throw;
        }
    }

    public async Task DeleteStarlinkConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.StarlinkConfigurations.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.StarlinkConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Deleted Starlink configuration {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete Starlink configuration {Id}", id);
            throw;
        }
    }
}
