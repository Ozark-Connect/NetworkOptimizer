using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for UniFi connection, SSH settings, and device configurations
/// </summary>
public class UniFiRepository : IUniFiRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<UniFiRepository> _logger;

    public UniFiRepository(NetworkOptimizerDbContext context, ILogger<UniFiRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Connection Settings

    /// <summary>
    /// Retrieves the UniFi controller connection settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection settings, or null if not configured.</returns>
    public async Task<UniFiConnectionSettings?> GetUniFiConnectionSettingsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.UniFiConnectionSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.SiteId == siteId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get UniFi connection settings for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates the UniFi controller connection settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="settings">The connection settings to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveUniFiConnectionSettingsAsync(int siteId, UniFiConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.UniFiConnectionSettings
                .FirstOrDefaultAsync(c => c.SiteId == siteId, cancellationToken);
            if (existing != null)
            {
                existing.ControllerUrl = settings.ControllerUrl;
                existing.Username = settings.Username;
                existing.Password = settings.Password;
                existing.UniFiSiteId = settings.UniFiSiteId;
                existing.IgnoreControllerSSLErrors = settings.IgnoreControllerSSLErrors;
                existing.RememberCredentials = settings.RememberCredentials;
                existing.IsConfigured = settings.IsConfigured;
                existing.LastConnectedAt = settings.LastConnectedAt;
                existing.LastError = settings.LastError;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                settings.SiteId = siteId;
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.UniFiConnectionSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved UniFi connection settings for {Url} in site {SiteId}", settings.ControllerUrl, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save UniFi connection settings for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion

    #region SSH Settings

    /// <summary>
    /// Retrieves the UniFi device SSH settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SSH settings, or null if not configured.</returns>
    public async Task<UniFiSshSettings?> GetUniFiSshSettingsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.UniFiSshSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SiteId == siteId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get UniFi SSH settings for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates the UniFi device SSH settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="settings">The SSH settings to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveUniFiSshSettingsAsync(int siteId, UniFiSshSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.UniFiSshSettings
                .FirstOrDefaultAsync(s => s.SiteId == siteId, cancellationToken);
            if (existing != null)
            {
                existing.Port = settings.Port;
                existing.Username = settings.Username;
                existing.Password = settings.Password;
                existing.PrivateKeyPath = settings.PrivateKeyPath;
                existing.Enabled = settings.Enabled;
                existing.LastTestedAt = settings.LastTestedAt;
                existing.LastTestResult = settings.LastTestResult;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                settings.SiteId = siteId;
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.UniFiSshSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved UniFi SSH settings for site {SiteId}", siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save UniFi SSH settings for site {SiteId}", siteId);
            throw;
        }
    }

    #endregion

    #region Device SSH Configurations

    /// <summary>
    /// Retrieves all device-specific SSH configurations for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of device SSH configurations ordered by name.</returns>
    public async Task<List<DeviceSshConfiguration>> GetDeviceSshConfigurationsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeviceSshConfigurations
                .AsNoTracking()
                .Where(d => d.SiteId == siteId)
                .OrderBy(d => d.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get device SSH configurations for site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves a specific device SSH configuration by ID for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="id">The configuration ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The device configuration, or null if not found.</returns>
    public async Task<DeviceSshConfiguration?> GetDeviceSshConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeviceSshConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.SiteId == siteId && d.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get device SSH configuration {Id} for site {SiteId}", id, siteId);
            throw;
        }
    }

    /// <summary>
    /// Saves or updates a device-specific SSH configuration for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="config">The device configuration to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveDeviceSshConfigurationAsync(int siteId, DeviceSshConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.DeviceSshConfigurations
                    .FirstOrDefaultAsync(d => d.SiteId == siteId && d.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.Host = config.Host;
                    existing.DeviceType = config.DeviceType;
                    existing.Enabled = config.Enabled;
                    existing.StartIperf3Server = config.StartIperf3Server;
                    existing.Iperf3BinaryPath = config.Iperf3BinaryPath;
                    existing.SshUsername = config.SshUsername;
                    existing.SshPassword = config.SshPassword;
                    existing.SshPrivateKeyPath = config.SshPrivateKeyPath;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                config.SiteId = siteId;
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.DeviceSshConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved device SSH configuration {Name} ({Host}) for site {SiteId}", config.Name, config.Host, siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save device SSH configuration {Name} for site {SiteId}", config.Name, siteId);
            throw;
        }
    }

    /// <summary>
    /// Deletes a device SSH configuration by ID for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="id">The configuration ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteDeviceSshConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.DeviceSshConfigurations
                .FirstOrDefaultAsync(d => d.SiteId == siteId && d.Id == id, cancellationToken);
            if (config != null)
            {
                _context.DeviceSshConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted device SSH configuration {Id} from site {SiteId}", id, siteId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete device SSH configuration {Id} from site {SiteId}", id, siteId);
            throw;
        }
    }

    #endregion
}
