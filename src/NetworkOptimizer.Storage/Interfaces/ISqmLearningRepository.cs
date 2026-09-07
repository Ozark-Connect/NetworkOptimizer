using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Adaptive SQM congestion profile learning: the per-WAN profile row and its raw samples.
/// </summary>
public interface ISqmLearningRepository
{
    Task<SqmCongestionProfile?> GetProfileAsync(int wanNumber, CancellationToken cancellationToken = default);
    Task<List<SqmCongestionProfile>> GetAllProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates the row for the profile's WAN number.</summary>
    Task SaveProfileAsync(SqmCongestionProfile profile, CancellationToken cancellationToken = default);

    /// <summary>Deletes the profile row and every sample for the WAN.</summary>
    Task DeleteProfileAsync(int wanNumber, CancellationToken cancellationToken = default);

    Task<int> AddSampleAsync(SqmLearningSample sample, CancellationToken cancellationToken = default);
    Task<List<SqmLearningSample>> GetSamplesAsync(int wanNumber, DateTime? sinceUtc = null, CancellationToken cancellationToken = default);

    /// <summary>Applies the learner's exclusions: ids in the map are excluded with their reason, every other sample of the WAN is cleared.</summary>
    Task SetSampleExclusionsAsync(int wanNumber, IReadOnlyDictionary<int, string> exclusions, CancellationToken cancellationToken = default);

    Task DeleteSamplesAsync(int wanNumber, CancellationToken cancellationToken = default);
}
