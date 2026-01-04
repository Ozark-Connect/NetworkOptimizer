using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing client-initiated speed tests (browser-based and iperf3 clients).
/// </summary>
public class ClientSpeedTestService
{
    private readonly ILogger<ClientSpeedTestService> _logger;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;

    public ClientSpeedTestService(
        ILogger<ClientSpeedTestService> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _connectionService = connectionService;
    }

    /// <summary>
    /// Record a speed test result from OpenSpeedTest browser client.
    /// </summary>
    public async Task<ClientSpeedTestResult> RecordOpenSpeedTestResultAsync(
        string clientIp,
        double downloadMbps,
        double uploadMbps,
        double? pingMs,
        double? jitterMs,
        double? downloadDataMb,
        double? uploadDataMb,
        string? userAgent)
    {
        var result = new ClientSpeedTestResult
        {
            Source = ClientSpeedTestSource.OpenSpeedTest,
            ClientIp = clientIp,
            DownloadMbps = downloadMbps,
            UploadMbps = uploadMbps,
            PingMs = pingMs,
            JitterMs = jitterMs,
            DownloadDataMb = downloadDataMb,
            UploadDataMb = uploadDataMb,
            UserAgent = userAgent,
            TestTime = DateTime.UtcNow,
            Success = true
        };

        // Try to look up client info from UniFi
        await EnrichClientInfoAsync(result);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.ClientSpeedTestResults.Add(result);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Recorded OpenSpeedTest result: {ClientIp} ({ClientName}) - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps",
            result.ClientIp, result.ClientName ?? "Unknown", result.DownloadMbps, result.UploadMbps);

        return result;
    }

    /// <summary>
    /// Record a speed test result from an iperf3 client.
    /// </summary>
    public async Task<ClientSpeedTestResult> RecordIperf3ClientResultAsync(
        string clientIp,
        double downloadBitsPerSecond,
        double uploadBitsPerSecond,
        int? downloadRetransmits,
        int? uploadRetransmits,
        int durationSeconds,
        int parallelStreams,
        string? rawJson)
    {
        var result = new ClientSpeedTestResult
        {
            Source = ClientSpeedTestSource.Iperf3Client,
            ClientIp = clientIp,
            DownloadMbps = downloadBitsPerSecond / 1_000_000.0,
            UploadMbps = uploadBitsPerSecond / 1_000_000.0,
            DownloadRetransmits = downloadRetransmits,
            UploadRetransmits = uploadRetransmits,
            DurationSeconds = durationSeconds,
            ParallelStreams = parallelStreams,
            RawJson = rawJson,
            TestTime = DateTime.UtcNow,
            Success = true
        };

        // Try to look up client info from UniFi
        await EnrichClientInfoAsync(result);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.ClientSpeedTestResults.Add(result);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Recorded iperf3 client result: {ClientIp} ({ClientName}) - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps",
            result.ClientIp, result.ClientName ?? "Unknown", result.DownloadMbps, result.UploadMbps);

        return result;
    }

    /// <summary>
    /// Get recent client speed test results.
    /// </summary>
    public async Task<List<ClientSpeedTestResult>> GetResultsAsync(int count = 50)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientSpeedTestResults
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Get client speed test results for a specific IP.
    /// </summary>
    public async Task<List<ClientSpeedTestResult>> GetResultsByIpAsync(string clientIp, int count = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientSpeedTestResults
            .Where(r => r.ClientIp == clientIp)
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Get client speed test results for a specific MAC.
    /// </summary>
    public async Task<List<ClientSpeedTestResult>> GetResultsByMacAsync(string clientMac, int count = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ClientSpeedTestResults
            .Where(r => r.ClientMac == clientMac)
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Enrich a result with client info from UniFi (MAC, name).
    /// </summary>
    private async Task EnrichClientInfoAsync(ClientSpeedTestResult result)
    {
        try
        {
            if (!_connectionService.IsConnected)
                return;

            var clients = await _connectionService.Client.GetClientsAsync();
            var client = clients?.FirstOrDefault(c => c.Ip == result.ClientIp);

            if (client != null)
            {
                result.ClientMac = client.Mac;
                result.ClientName = !string.IsNullOrEmpty(client.Name) ? client.Name : client.Hostname;
                _logger.LogDebug("Enriched client info for {Ip}: MAC={Mac}, Name={Name}",
                    result.ClientIp, result.ClientMac, result.ClientName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich client info for {Ip}", result.ClientIp);
        }
    }
}
