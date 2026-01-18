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

    /// <summary>
    /// Retrieves all modem configurations for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of modem configurations ordered by name.</returns>
    public async Task<List<ModemConfiguration>> GetModemConfigurationsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ModemConfigurations
                .AsNoTracking()
                .Where(m => m.SiteId == siteId)
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get modem configurations for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves only enabled modem configurations for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of enabled modem configurations ordered by name.</returns>
    public async Task<List<ModemConfiguration>> GetEnabledModemConfigurationsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ModemConfigurations
                .AsNoTracking()
                .Where(m => m.SiteId == siteId && m.Enabled)
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled modem configurations for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves a specific modem configuration by ID for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="id">The modem configuration ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The modem configuration, or null if not found.</returns>
    public async Task<ModemConfiguration?> GetModemConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ModemConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.SiteId == siteId && m.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get modem configuration {Id} for site {SiteId}", id, siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates a modem configuration for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="config">The modem configuration to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveModemConfigurationAsync(int siteId, ModemConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.ModemConfigurations
                    .FirstOrDefaultAsync(m => m.SiteId == siteId && m.Id == config.Id, cancellationToken);
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
                config.SiteId = siteId;
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.ModemConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved modem configuration {Name} ({Host}) for site {SiteId}", config.Name, config.Host, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save modem configuration {Name} for site {SiteId}", config.Name, siteId);
            throw;
        }
    }

    /// <summary>
    /// Deletes a modem configuration by ID for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="id">The modem configuration ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteModemConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.ModemConfigurations
                .FirstOrDefaultAsync(m => m.SiteId == siteId && m.Id == id, cancellationToken);
            if (config != null)
            {
                _context.ModemConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted modem configuration {Id} from site {SiteId}", id, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete modem configuration {Id} from site {SiteId}", id, siteId);
            throw;
        }
    }
}
