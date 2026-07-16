using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for Starlink terminal configurations
/// </summary>
public interface IStarlinkRepository
{
    Task<List<StarlinkConfiguration>> GetStarlinkConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<StarlinkConfiguration>> GetEnabledStarlinkConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<StarlinkConfiguration?> GetStarlinkConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveStarlinkConfigurationAsync(StarlinkConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteStarlinkConfigurationAsync(int id, CancellationToken cancellationToken = default);
}
