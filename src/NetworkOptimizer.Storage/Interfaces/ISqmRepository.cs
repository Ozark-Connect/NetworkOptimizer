using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for SQM baseline configurations
/// </summary>
public interface ISqmRepository
{
    Task<int> SaveSqmBaselineAsync(SqmBaseline baseline, CancellationToken cancellationToken = default);
    Task<SqmBaseline?> GetSqmBaselineAsync(string deviceId, string interfaceId, CancellationToken cancellationToken = default);
    Task<List<SqmBaseline>> GetAllSqmBaselinesAsync(string? deviceId = null, CancellationToken cancellationToken = default);
    Task UpdateSqmBaselineAsync(SqmBaseline baseline, CancellationToken cancellationToken = default);
    Task DeleteSqmBaselineAsync(int baselineId, CancellationToken cancellationToken = default);
}
