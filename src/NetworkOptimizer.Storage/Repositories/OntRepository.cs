using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for external ONT configurations
/// </summary>
public class OntRepository : IOntRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<OntRepository> _logger;

    public OntRepository(NetworkOptimizerDbContext context, ILogger<OntRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<OntConfiguration>> GetOntConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.OntConfigurations
                .AsNoTracking()
                .OrderBy(o => o.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get ONT configurations");
            throw;
        }
    }

    public async Task<List<OntConfiguration>> GetEnabledOntConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.OntConfigurations
                .AsNoTracking()
                .Where(o => o.Enabled)
                .OrderBy(o => o.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled ONT configurations");
            throw;
        }
    }

    public async Task<OntConfiguration?> GetOntConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.OntConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get ONT configuration {Id}", id);
            throw;
        }
    }

    public async Task SaveOntConfigurationAsync(OntConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.OntConfigurations
                    .FirstOrDefaultAsync(o => o.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.Provider = config.Provider;
                    existing.Host = config.Host;
                    existing.Port = config.Port;
                    existing.Username = config.Username;
                    existing.Password = config.Password;
                    existing.PrivateKeyPath = config.PrivateKeyPath;
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
                _context.OntConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogDebug("Saved ONT configuration {Name} ({Host})", config.Name, config.Host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save ONT configuration {Name}", config.Name);
            throw;
        }
    }

    public async Task DeleteOntConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.OntConfigurations.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.OntConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Deleted ONT configuration {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete ONT configuration {Id}", id);
            throw;
        }
    }
}
