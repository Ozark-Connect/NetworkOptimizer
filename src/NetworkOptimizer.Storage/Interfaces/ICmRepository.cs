using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for cable modem configurations
/// </summary>
public interface ICmRepository
{
    Task<List<CmConfiguration>> GetCmConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<CmConfiguration>> GetEnabledCmConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<CmConfiguration?> GetCmConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveCmConfigurationAsync(CmConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteCmConfigurationAsync(int id, CancellationToken cancellationToken = default);
}
