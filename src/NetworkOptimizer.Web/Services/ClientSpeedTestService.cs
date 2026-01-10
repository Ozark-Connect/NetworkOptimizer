using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing client-initiated speed tests (browser-based and iperf3 clients).
/// Uses the unified Iperf3Result table with Direction field to distinguish test types.
/// </summary>
public class ClientSpeedTestService
{
    private readonly ILogger<ClientSpeedTestService> _logger;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly IConfiguration _configuration;

    public ClientSpeedTestService(
        ILogger<ClientSpeedTestService> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        INetworkPathAnalyzer pathAnalyzer,
        IConfiguration configuration)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _pathAnalyzer = pathAnalyzer;
        _configuration = configuration;
    }

    /// <summary>
    /// Record a speed test result from OpenSpeedTest browser client.
    /// </summary>
    public async Task<Iperf3Result> RecordOpenSpeedTestResultAsync(
        string clientIp,
        double downloadMbps,
        double uploadMbps,
        double? pingMs,
        double? jitterMs,
        double? downloadDataMb,
        double? uploadDataMb,
        string? userAgent,
        double? latitude = null,
        double? longitude = null,
        int? locationAccuracy = null)
    {
        // Get server's local IP for path analysis
        var serverIp = _configuration["HOST_IP"];

        // Store from SERVER's perspective (consistent with SSH-based tests):
        // - DownloadBitsPerSecond = data server received FROM client = client's upload
        // - UploadBitsPerSecond = data server sent TO client = client's download
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.BrowserToServer,
            DeviceHost = clientIp,
            LocalIp = serverIp,
            DownloadBitsPerSecond = uploadMbps * 1_000_000.0,  // Client upload = server download
            UploadBitsPerSecond = downloadMbps * 1_000_000.0,  // Client download = server upload
            DownloadBytes = (long)((uploadDataMb ?? 0) * 1_048_576),  // MB to bytes
            UploadBytes = (long)((downloadDataMb ?? 0) * 1_048_576),  // MB to bytes
            PingMs = pingMs,
            JitterMs = jitterMs,
            UserAgent = userAgent,
            TestTime = DateTime.UtcNow,
            Success = true,
            ParallelStreams = 6,  // OpenSpeedTest default: 6 parallel HTTP connections
            // Geolocation (if provided)
            Latitude = latitude,
            Longitude = longitude,
            LocationAccuracyMeters = locationAccuracy
        };

        // Try to look up client info from UniFi
        await _connectionService.EnrichSpeedTestWithClientInfoAsync(result);

        // Perform path analysis (client to server)
        await AnalyzePathAsync(result);

        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Iperf3Results.Add(result);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Recorded OpenSpeedTest result: {ClientIp} ({ClientName}) - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps",
            result.DeviceHost, result.DeviceName ?? "Unknown", result.DownloadMbps, result.UploadMbps);

        return result;
    }

    /// <summary>
    /// Record a speed test result from an iperf3 client.
    /// Merges with recent result from same client if one direction is missing.
    /// </summary>
    public async Task<Iperf3Result> RecordIperf3ClientResultAsync(
        string clientIp,
        double downloadBitsPerSecond,
        double uploadBitsPerSecond,
        long downloadBytes,
        long uploadBytes,
        int? downloadRetransmits,
        int? uploadRetransmits,
        int durationSeconds,
        int parallelStreams,
        string? rawJson,
        string? serverLocalIp = null)
    {
        var now = DateTime.UtcNow;
        // Use the actual server IP from iperf3, fall back to HOST_IP config
        var serverIp = serverLocalIp ?? _configuration["HOST_IP"];

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Check for recent result from same client that we can merge with
        // (within 60 seconds, one has download but no upload, or vice versa)
        var mergeWindow = now.AddSeconds(-60);
        var recentResult = await db.Iperf3Results
            .Where(r => r.Direction == SpeedTestDirection.ClientToServer
                     && r.DeviceHost == clientIp
                     && r.TestTime > mergeWindow)
            .OrderByDescending(r => r.TestTime)
            .FirstOrDefaultAsync();

        // Determine if we can merge: one result has download only, the other has upload only
        bool canMerge = recentResult != null
            && ((recentResult.DownloadBitsPerSecond > 0 && recentResult.UploadBitsPerSecond == 0 && uploadBitsPerSecond > 0 && downloadBitsPerSecond == 0)
             || (recentResult.UploadBitsPerSecond > 0 && recentResult.DownloadBitsPerSecond == 0 && downloadBitsPerSecond > 0 && uploadBitsPerSecond == 0));

        if (canMerge && recentResult != null)
        {
            // Merge: fill in the missing direction
            if (downloadBitsPerSecond > 0)
            {
                recentResult.DownloadBitsPerSecond = downloadBitsPerSecond;
                recentResult.DownloadBytes = downloadBytes;
                recentResult.DownloadRetransmits = downloadRetransmits ?? 0;
            }
            if (uploadBitsPerSecond > 0)
            {
                recentResult.UploadBitsPerSecond = uploadBitsPerSecond;
                recentResult.UploadBytes = uploadBytes;
                recentResult.UploadRetransmits = uploadRetransmits ?? 0;
            }

            // Use max parallel streams from either test
            if (parallelStreams > recentResult.ParallelStreams)
                recentResult.ParallelStreams = parallelStreams;

            // Re-analyze path with updated bidirectional data
            await AnalyzePathAsync(recentResult);

            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Merged iperf3 result: {ClientIp} ({ClientName}) - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps ({Streams} streams)",
                recentResult.DeviceHost, recentResult.DeviceName ?? "Unknown",
                recentResult.DownloadMbps, recentResult.UploadMbps, recentResult.ParallelStreams);

            return recentResult;
        }

        // No merge - create new result
        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.ClientToServer,
            DeviceHost = clientIp,
            LocalIp = serverIp,
            DownloadBitsPerSecond = downloadBitsPerSecond,
            UploadBitsPerSecond = uploadBitsPerSecond,
            DownloadBytes = downloadBytes,
            UploadBytes = uploadBytes,
            DownloadRetransmits = downloadRetransmits ?? 0,
            UploadRetransmits = uploadRetransmits ?? 0,
            DurationSeconds = durationSeconds,
            ParallelStreams = parallelStreams,
            RawDownloadJson = rawJson, // Store in RawDownloadJson for client tests
            TestTime = now,
            Success = true
        };

        // Try to look up client info from UniFi
        await _connectionService.EnrichSpeedTestWithClientInfoAsync(result);

        // Perform path analysis
        await AnalyzePathAsync(result);

        db.Iperf3Results.Add(result);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Recorded iperf3 client result: {ClientIp} ({ClientName}) - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps ({Streams} streams)",
            result.DeviceHost, result.DeviceName ?? "Unknown", result.DownloadMbps, result.UploadMbps, parallelStreams);

        return result;
    }

    /// <summary>
    /// Get recent client speed test results (ClientToServer and BrowserToServer directions).
    /// Retries path analysis for results missing valid paths.
    /// </summary>
    /// <param name="count">Maximum number of results (0 = no limit)</param>
    /// <param name="hours">Filter to results within the last N hours (0 = all time)</param>
    public async Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Iperf3Results
            .Where(r => r.Direction == SpeedTestDirection.ClientToServer
                     || r.Direction == SpeedTestDirection.BrowserToServer);

        // Apply date filter if specified
        if (hours > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-hours);
            query = query.Where(r => r.TestTime >= cutoff);
        }

        query = query.OrderByDescending(r => r.TestTime);

        // Apply count limit if specified
        if (count > 0)
        {
            query = query.Take(count);
        }

        var results = await query.ToListAsync();

        // Retry path analysis for recent results (last 30 min) without a valid path
        // Skip IPs that will never be in UniFi topology (Tailscale, external VPNs, etc.)
        var retryWindow = DateTime.UtcNow.AddMinutes(-30);
        var needsRetry = results.Where(r =>
            r.TestTime > retryWindow &&
            !IsNonRoutableIp(r.DeviceHost) &&
            (r.PathAnalysis == null ||
             r.PathAnalysis.Path == null ||
             !r.PathAnalysis.Path.IsValid))
            .ToList();

        if (needsRetry.Count > 0)
        {
            _logger.LogInformation("Retrying path analysis for {Count} results without valid paths", needsRetry.Count);
            foreach (var result in needsRetry)
            {
                await AnalyzePathAsync(result);
            }
            await db.SaveChangesAsync();
        }

        return results;
    }

    /// <summary>
    /// Checks if an IP is non-routable through local UniFi infrastructure.
    /// These IPs will never appear in UniFi topology so path analysis should not be retried.
    /// </summary>
    private static bool IsNonRoutableIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
            return true;

        // Tailscale/CGNAT range: 100.64.0.0/10 (100.64.0.0 - 100.127.255.255)
        // Tailscale uses this for its virtual IPs
        if (ip.StartsWith("100."))
        {
            if (int.TryParse(ip.Split('.')[1], out int secondOctet))
            {
                if (secondOctet >= 64 && secondOctet <= 127)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get client speed test results for a specific IP.
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsByIpAsync(string clientIp, int count = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Iperf3Results
            .Where(r => (r.Direction == SpeedTestDirection.ClientToServer
                      || r.Direction == SpeedTestDirection.BrowserToServer)
                     && r.DeviceHost == clientIp)
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Get client speed test results for a specific MAC.
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsByMacAsync(string clientMac, int count = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Iperf3Results
            .Where(r => (r.Direction == SpeedTestDirection.ClientToServer
                      || r.Direction == SpeedTestDirection.BrowserToServer)
                     && r.ClientMac == clientMac)
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Analyze network path for the speed test result.
    /// For client tests, the path is from server (LocalIp) to client (DeviceHost).
    /// If target not found, invalidates topology cache and retries once.
    /// </summary>
    private async Task AnalyzePathAsync(Iperf3Result result, bool isRetry = false)
    {
        try
        {
            _logger.LogDebug("Analyzing network path to {Client} from {Server}{Retry}",
                result.DeviceHost, result.LocalIp ?? "auto", isRetry ? " (retry)" : "");

            // Calculate path from server to client
            var path = await _pathAnalyzer.CalculatePathAsync(result.DeviceHost, result.LocalIp);

            // Analyze speed test against the path
            var analysis = _pathAnalyzer.AnalyzeSpeedTest(
                path,
                result.DownloadMbps,
                result.UploadMbps,
                result.DownloadRetransmits,
                result.UploadRetransmits,
                result.DownloadBytes,
                result.UploadBytes);

            result.PathAnalysis = analysis;

            if (analysis.Path.IsValid)
            {
                _logger.LogDebug("Path analysis complete: {Hops} hops",
                    analysis.Path.Hops.Count);
            }
            else
            {
                // If target not found or data stale and this isn't already a retry, invalidate cache and try again
                var errorMsg = analysis.Path.ErrorMessage ?? "";
                var shouldRetry = !isRetry && (
                    errorMsg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    errorMsg.Contains("not yet available", StringComparison.OrdinalIgnoreCase));

                if (shouldRetry)
                {
                    _logger.LogDebug("Path invalid ({Reason}), invalidating topology cache and retrying",
                        errorMsg.Contains("not yet") ? "stale data" : "target not found");
                    _pathAnalyzer.InvalidateTopologyCache();
                    await AnalyzePathAsync(result, isRetry: true);
                }
                else
                {
                    _logger.LogDebug("Path analysis: path not found or invalid");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze path for {Client}", result.DeviceHost);
        }
    }
}
