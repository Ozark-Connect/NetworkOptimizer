using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for modem configurations
/// </summary>
public class ModemRepository : IModemRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<ModemRepository> _logger;

    public ModemRepository(NetworkOptimizerDbContext context, ILogger<ModemRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<ModemConfiguration>> GetModemConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ModemConfigurations
                .AsNoTracking()
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get modem configurations");
            throw;
        }
    }

    public async Task<List<ModemConfiguration>> GetEnabledModemConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ModemConfigurations
                .AsNoTracking()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled modem configurations");
            throw;
        }
    }

    public async Task<ModemConfiguration?> GetModemConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ModemConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get modem configuration {Id}", id);
            throw;
        }
    }

    public async Task SaveModemConfigurationAsync(ModemConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.ModemConfigurations
                    .FirstOrDefaultAsync(m => m.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.Host = config.Host;
                    existing.Port = config.Port;
                    existing.Username = config.Username;
                    existing.Password = config.Password;
                    existing.PrivateKeyPath = config.PrivateKeyPath;
                    existing.ModemType = config.ModemType;
                    existing.QmiDevice = config.QmiDevice;
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
                _context.ModemConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved modem configuration {Name} ({Host})", config.Name, config.Host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save modem configuration {Name}", config.Name);
            throw;
        }
    }

    public async Task DeleteModemConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.ModemConfigurations.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.ModemConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted modem configuration {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete modem configuration {Id}", id);
            throw;
        }
    }
}
