using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

// TODO: Rename to IGatewayRepository - this interface handles gateway SSH settings,
// iperf3 speed test results, and SQM WAN configuration. "SpeedTestRepository" is misleading.
// Refactor all usages across the codebase when renaming.

/// <summary>
/// Repository for gateway SSH settings and iperf3 speed test results
/// </summary>
public interface ISpeedTestRepository
{
    // Gateway SSH Settings
    Task<GatewaySshSettings?> GetGatewaySshSettingsAsync(int siteId, CancellationToken cancellationToken = default);
    Task SaveGatewaySshSettingsAsync(int siteId, GatewaySshSettings settings, CancellationToken cancellationToken = default);

    // Iperf3 Results
    Task SaveIperf3ResultAsync(int siteId, Iperf3Result result, CancellationToken cancellationToken = default);
    Task<List<Iperf3Result>> GetRecentIperf3ResultsAsync(int siteId, int count = 50, int hours = 0, CancellationToken cancellationToken = default);
    Task<List<Iperf3Result>> GetIperf3ResultsForDeviceAsync(int siteId, string deviceHost, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches speed test results by device name, host, MAC, or network path involvement.
    /// </summary>
    /// <param name="siteId">Site ID to filter results</param>
    /// <param name="filter">Search filter (matches device name, host, client MAC, or hop names/MACs in path)</param>
    /// <param name="count">Maximum number of results to return (0 = no limit)</param>
    /// <param name="hours">Filter to results within the last N hours (0 = all time)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching results ordered by time descending</returns>
    Task<List<Iperf3Result>> SearchIperf3ResultsAsync(int siteId, string filter, int count = 50, int hours = 0, CancellationToken cancellationToken = default);

    Task<bool> DeleteIperf3ResultAsync(int siteId, int id, CancellationToken cancellationToken = default);
    Task ClearIperf3HistoryAsync(int siteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the notes for a speed test result.
    /// </summary>
    /// <param name="id">Result ID</param>
    /// <param name="notes">Notes text (null or empty to clear)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the result was found and updated</returns>
    Task<bool> UpdateIperf3ResultNotesAsync(int id, string? notes, CancellationToken cancellationToken = default);

    // SQM WAN Configuration
    Task<SqmWanConfiguration?> GetSqmWanConfigAsync(int siteId, int wanNumber, CancellationToken cancellationToken = default);
    Task<List<SqmWanConfiguration>> GetAllSqmWanConfigsAsync(int siteId, CancellationToken cancellationToken = default);
    Task SaveSqmWanConfigAsync(int siteId, SqmWanConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteSqmWanConfigAsync(int siteId, int wanNumber, CancellationToken cancellationToken = default);
    Task ClearAllSqmWanConfigsAsync(int siteId, CancellationToken cancellationToken = default);
}
