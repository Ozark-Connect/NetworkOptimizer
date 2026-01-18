using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for modem configurations
/// </summary>
public interface IModemRepository
{
    Task<List<ModemConfiguration>> GetModemConfigurationsAsync(int siteId, CancellationToken cancellationToken = default);
    Task<List<ModemConfiguration>> GetEnabledModemConfigurationsAsync(int siteId, CancellationToken cancellationToken = default);
    Task<ModemConfiguration?> GetModemConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default);
    Task SaveModemConfigurationAsync(int siteId, ModemConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteModemConfigurationAsync(int siteId, int id, CancellationToken cancellationToken = default);
}
