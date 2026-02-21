namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// Provides access to threat-related system settings without coupling to Storage.
/// Implemented in the Web project using IDbContextFactory.
/// </summary>
public interface IThreatSettingsAccessor
{
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
    Task<string?> GetDecryptedSettingAsync(string key, CancellationToken cancellationToken = default);
    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default);
}
