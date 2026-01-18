using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for UniFi connection, SSH settings, and device configurations
/// </summary>
public interface IUniFiRepository
{
    // Connection Settings
    Task<UniFiConnectionSettings?> GetUniFiConnectionSettingsAsync(int siteId, CancellationToken cancellationToken = default);
    Task SaveUniFiConnectionSettingsAsync(int siteId, UniFiConnectionSettings settings, CancellationToken cancellationToken = default);

    // SSH Settings
    Task<UniFiSshSettings?> GetUniFiSshSettingsAsync(int siteId, CancellationToken cancellationToken = default);
    Task SaveUniFiSshSettingsAsync(int siteId, UniFiSshSettings settings, CancellationToken cancellationToken = default);

    // Device SSH Configurations
    Task<List<DeviceSshConfiguration>> GetDeviceSshConfigurationsAsync(int siteId, CancellationToken cancellationToken = default);
    Task<DeviceSshConfiguration?> GetDeviceSshConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default);
    Task SaveDeviceSshConfigurationAsync(int siteId, DeviceSshConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteDeviceSshConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default);
}
