using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for external ONT configurations
/// </summary>
public interface IOntRepository
{
    Task<List<OntConfiguration>> GetOntConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<List<OntConfiguration>> GetEnabledOntConfigurationsAsync(CancellationToken cancellationToken = default);
    Task<OntConfiguration?> GetOntConfigurationAsync(int id, CancellationToken cancellationToken = default);
    Task SaveOntConfigurationAsync(OntConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteOntConfigurationAsync(int id, CancellationToken cancellationToken = default);
}
