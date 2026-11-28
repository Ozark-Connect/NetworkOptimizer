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
    Task<GatewaySshSettings?> GetGatewaySshSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveGatewaySshSettingsAsync(GatewaySshSettings settings, CancellationToken cancellationToken = default);

    // Iperf3 Results
    Task SaveIperf3ResultAsync(Iperf3Result result, CancellationToken cancellationToken = default);
    Task<List<Iperf3Result>> GetRecentIperf3ResultsAsync(int count = 50, int hours = 0, CancellationToken cancellationToken = default);
    Task<List<Iperf3Result>> GetIperf3ResultsForDeviceAsync(string deviceHost, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches speed test results by device name, host, MAC, or network path involvement.
    /// </summary>
    /// <param name="filter">Search filter (matches device name, host, client MAC, or hop names/MACs in path)</param>
    /// <param name="count">Maximum number of results to return (0 = no limit)</param>
    /// <param name="hours">Filter to results within the last N hours (0 = all time)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching results ordered by time descending</returns>
    Task<List<Iperf3Result>> SearchIperf3ResultsAsync(string filter, int count = 50, int hours = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of LAN results (WAN tests excluded), newest first, filtered and counted in the database.
    /// Items carry every column except the raw iperf3 JSON.
    /// </summary>
    /// <param name="filter">Search text and device host to narrow by</param>
    /// <param name="page">1-based page; clamped to the last page</param>
    /// <param name="pageSize">Results per page</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<SpeedTestHistoryPage> GetLanHistoryPageAsync(SpeedTestHistoryFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// The 0-based position of a result in the filtered LAN history's newest-first order, or null when the
    /// filter excludes it or it does not exist.
    /// </summary>
    Task<int?> GetLanHistoryIndexAsync(SpeedTestHistoryFilter filter, int resultId, CancellationToken cancellationToken = default);

    /// <summary>
    /// LAN results (WAN tests excluded) at or after <paramref name="since"/>, newest first, without the raw
    /// iperf3 JSON.
    /// </summary>
    /// <param name="since">Oldest test time to include; null for all time</param>
    /// <param name="direction">Only this direction; null for every LAN direction</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<Iperf3Result>> GetLanResultsAsync(DateTime? since, SpeedTestDirection? direction = null, CancellationToken cancellationToken = default);

    Task<bool> DeleteIperf3ResultAsync(int id, CancellationToken cancellationToken = default);
    Task ClearIperf3HistoryAsync(CancellationToken cancellationToken = default);
    Task ClearAllIperf3ResultsAsync(CancellationToken cancellationToken = default);
    Task<int> GetIperf3ResultCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the notes for a speed test result.
    /// </summary>
    /// <param name="id">Result ID</param>
    /// <param name="notes">Notes text (null or empty to clear)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the result was found and updated</returns>
    Task<bool> UpdateIperf3ResultNotesAsync(int id, string? notes, CancellationToken cancellationToken = default);

    // SQM WAN Configuration
    Task<SqmWanConfiguration?> GetSqmWanConfigAsync(int wanNumber, CancellationToken cancellationToken = default);
    Task<List<SqmWanConfiguration>> GetAllSqmWanConfigsAsync(CancellationToken cancellationToken = default);
    Task SaveSqmWanConfigAsync(SqmWanConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteSqmWanConfigAsync(int wanNumber, CancellationToken cancellationToken = default);
    Task ClearAllSqmWanConfigsAsync(CancellationToken cancellationToken = default);
}
