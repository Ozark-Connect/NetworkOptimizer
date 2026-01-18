using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for SQM baseline configurations
/// </summary>
public interface ISqmRepository
{
    Task<int> SaveSqmBaselineAsync(int siteId, SqmBaseline baseline, CancellationToken cancellationToken = default);
    Task<SqmBaseline?> GetSqmBaselineAsync(int siteId, string deviceId, string interfaceId, CancellationToken cancellationToken = default);
    Task<List<SqmBaseline>> GetAllSqmBaselinesAsync(int siteId, string? deviceId = null, CancellationToken cancellationToken = default);
    Task UpdateSqmBaselineAsync(int siteId, SqmBaseline baseline, CancellationToken cancellationToken = default);
    Task DeleteSqmBaselineAsync(int siteId, int baselineId, CancellationToken cancellationToken = default);
}
