using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for modem configurations
/// </summary>
public interface IModemRepository
{
    Task<List<ModemConfiguration>> GetModemConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<ModemConfiguration>> GetEnabledModemConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<ModemConfiguration?> GetModemConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveModemConfigurationAsync(ModemConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteModemConfigurationAsync(int id, CancellationToken cancellationToken = default);
}
