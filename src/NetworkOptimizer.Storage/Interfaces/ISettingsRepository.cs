using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for system settings and license information
/// </summary>
public interface ISettingsRepository
{
    // System Settings
    Task<string?> GetSystemSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SaveSystemSettingAsync(string key, string? value, CancellationToken cancellationToken = default);

    // License Information
    Task<int> SaveLicenseAsync(LicenseInfo license, CancellationToken cancellationToken = default);
    Task<LicenseInfo?> GetLicenseAsync(CancellationToken cancellationToken = default);
    Task UpdateLicenseAsync(LicenseInfo license, CancellationToken cancellationToken = default);

    // Admin Settings
    Task<AdminSettings?> GetAdminSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveAdminSettingsAsync(AdminSettings settings, CancellationToken cancellationToken = default);
}
