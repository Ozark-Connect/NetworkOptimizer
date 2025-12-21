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

    public async Task<UniFiConnectionSettings?> GetUniFiConnectionSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.UniFiConnectionSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get UniFi connection settings");
            throw;
        }
    }

    public async Task SaveUniFiConnectionSettingsAsync(UniFiConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.UniFiConnectionSettings.FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                existing.ControllerUrl = settings.ControllerUrl;
                existing.Username = settings.Username;
                existing.Password = settings.Password;
                existing.Site = settings.Site;
                existing.RememberCredentials = settings.RememberCredentials;
                existing.IsConfigured = settings.IsConfigured;
                existing.LastConnectedAt = settings.LastConnectedAt;
                existing.LastError = settings.LastError;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.UniFiConnectionSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved UniFi connection settings for {Url}", settings.ControllerUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save UniFi connection settings");
            throw;
        }
    }

    #endregion

    #region SSH Settings

    public async Task<UniFiSshSettings?> GetUniFiSshSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.UniFiSshSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get UniFi SSH settings");
            throw;
        }
    }

    public async Task SaveUniFiSshSettingsAsync(UniFiSshSettings settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.UniFiSshSettings.FirstOrDefaultAsync(cancellationToken);
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
                settings.CreatedAt = DateTime.UtcNow;
                settings.UpdatedAt = DateTime.UtcNow;
                _context.UniFiSshSettings.Add(settings);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved UniFi SSH settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save UniFi SSH settings");
            throw;
        }
    }

    #endregion

    #region Device SSH Configurations

    public async Task<List<DeviceSshConfiguration>> GetDeviceSshConfigurationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeviceSshConfigurations
                .AsNoTracking()
                .OrderBy(d => d.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get device SSH configurations");
            throw;
        }
    }

    public async Task<DeviceSshConfiguration?> GetDeviceSshConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.DeviceSshConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get device SSH configuration {Id}", id);
            throw;
        }
    }

    public async Task SaveDeviceSshConfigurationAsync(DeviceSshConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            if (config.Id > 0)
            {
                var existing = await _context.DeviceSshConfigurations
                    .FirstOrDefaultAsync(d => d.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.Host = config.Host;
                    existing.DeviceType = config.DeviceType;
                    existing.Enabled = config.Enabled;
                    existing.StartIperf3Server = config.StartIperf3Server;
                    existing.SshUsername = config.SshUsername;
                    existing.SshPassword = config.SshPassword;
                    existing.SshPrivateKeyPath = config.SshPrivateKeyPath;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.DeviceSshConfigurations.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved device SSH configuration {Name} ({Host})", config.Name, config.Host);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save device SSH configuration {Name}", config.Name);
            throw;
        }
    }

    public async Task DeleteDeviceSshConfigurationAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.DeviceSshConfigurations.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.DeviceSshConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted device SSH configuration {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete device SSH configuration {Id}", id);
            throw;
        }
    }

    #endregion
}
