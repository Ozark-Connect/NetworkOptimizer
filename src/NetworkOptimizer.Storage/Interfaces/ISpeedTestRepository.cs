using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

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
    Task<List<Iperf3Result>> GetRecentIperf3ResultsAsync(int count = 50, CancellationToken cancellationToken = default);
    Task<List<Iperf3Result>> GetIperf3ResultsForDeviceAsync(string deviceHost, int count = 50, CancellationToken cancellationToken = default);
    Task ClearIperf3HistoryAsync(CancellationToken cancellationToken = default);

    // SQM WAN Configuration
    Task<SqmWanConfiguration?> GetSqmWanConfigAsync(int wanNumber, CancellationToken cancellationToken = default);
    Task<List<SqmWanConfiguration>> GetAllSqmWanConfigsAsync(CancellationToken cancellationToken = default);
    Task SaveSqmWanConfigAsync(SqmWanConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteSqmWanConfigAsync(int wanNumber, CancellationToken cancellationToken = default);
    Task ClearAllSqmWanConfigsAsync(CancellationToken cancellationToken = default);
}
