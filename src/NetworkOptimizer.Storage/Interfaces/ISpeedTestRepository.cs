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
    Task ClearIperf3HistoryAsync(int siteId, CancellationToken cancellationToken = default);

    // SQM WAN Configuration
    Task<SqmWanConfiguration?> GetSqmWanConfigAsync(int siteId, int wanNumber, CancellationToken cancellationToken = default);
    Task<List<SqmWanConfiguration>> GetAllSqmWanConfigsAsync(int siteId, CancellationToken cancellationToken = default);
    Task SaveSqmWanConfigAsync(int siteId, SqmWanConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteSqmWanConfigAsync(int siteId, int wanNumber, CancellationToken cancellationToken = default);
    Task ClearAllSqmWanConfigsAsync(int siteId, CancellationToken cancellationToken = default);
}
