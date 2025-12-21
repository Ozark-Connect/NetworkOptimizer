using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for UniFi connection, SSH settings, and device configurations
/// </summary>
public interface IUniFiRepository
{
    // Connection Settings
    Task<UniFiConnectionSettings?> GetUniFiConnectionSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveUniFiConnectionSettingsAsync(UniFiConnectionSettings settings, CancellationToken cancellationToken = default);

    // SSH Settings
    Task<UniFiSshSettings?> GetUniFiSshSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveUniFiSshSettingsAsync(UniFiSshSettings settings, CancellationToken cancellationToken = default);

    // Device SSH Configurations
    Task<List<DeviceSshConfiguration>> GetDeviceSshConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<DeviceSshConfiguration?> GetDeviceSshConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveDeviceSshConfigurationAsync(DeviceSshConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteDeviceSshConfigurationAsync(int id, CancellationToken cancellationToken = default);
}
