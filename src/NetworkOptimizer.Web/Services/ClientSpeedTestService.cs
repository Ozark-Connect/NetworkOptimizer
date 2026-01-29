using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

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
    private readonly TopologySnapshotService _snapshotService;
    private readonly IConfiguration _configuration;

    public ClientSpeedTestService(
        ILogger<ClientSpeedTestService> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        INetworkPathAnalyzer pathAnalyzer,
        TopologySnapshotService snapshotService,
        IConfiguration configuration)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _pathAnalyzer = pathAnalyzer;
        _snapshotService = snapshotService;
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

        // Save immediately so client doesn't wait
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Iperf3Results.Add(result);
        await db.SaveChangesAsync();
        var resultId = result.Id;

        _logger.LogInformation(
            "Recorded OpenSpeedTest result: {ClientIp} - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps",
            result.DeviceHost, result.DownloadMbps, result.UploadMbps);

        // Enrich and analyze in background (after WiFi rates stabilize)
        _ = Task.Run(async () => await EnrichAndAnalyzeInBackgroundAsync(resultId));

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

            // Get snapshot captured during first direction test (if available)
            var snapshot = _snapshotService.GetSnapshot(clientIp);

            // Re-analyze path with updated bidirectional data (using snapshot for max rates)
            await AnalyzePathAsync(recentResult, snapshot);

            // Update WiFi rate fields from path analysis max values
            UpdateWifiRatesFromPathAnalysis(recentResult);

            // Clean up snapshot after use
            if (snapshot != null)
                _snapshotService.RemoveSnapshot(clientIp);

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

        // Save immediately so client doesn't wait
        db.Iperf3Results.Add(result);
        await db.SaveChangesAsync();
        var resultId = result.Id;

        _logger.LogInformation(
            "Recorded iperf3 client result: {ClientIp} - Down: {Download:F1} Mbps, Up: {Upload:F1} Mbps ({Streams} streams)",
            result.DeviceHost, result.DownloadMbps, result.UploadMbps, parallelStreams);

        // Capture snapshot now (during active test) for use when second direction merges
        // Fire-and-forget - don't block the response
        _ = _snapshotService.CaptureSnapshotAsync(clientIp);

        // Enrich and analyze in background (after WiFi rates stabilize)
        _ = Task.Run(async () => await EnrichAndAnalyzeInBackgroundAsync(resultId));

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
        var retryWindow = DateTime.UtcNow.AddMinutes(-30);
        var needsRetry = results.Where(r =>
            r.TestTime > retryWindow &&
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
    /// Delete a speed test result by ID.
    /// </summary>
    /// <returns>True if the result was deleted, false if not found.</returns>
    public async Task<bool> DeleteResultAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null)
        {
            return false;
        }

        db.Iperf3Results.Remove(result);
        await db.SaveChangesAsync();
        _logger.LogInformation("Deleted speed test result {Id} for {DeviceHost}", id, result.DeviceHost);
        return true;
    }

    /// <summary>
    /// Updates the notes for a speed test result.
    /// </summary>
    public async Task<bool> UpdateNotesAsync(int id, string? notes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null)
        {
            return false;
        }

        result.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync();
        _logger.LogDebug("Updated notes for speed test result {Id}", id);
        return true;
    }

    /// <summary>
    /// Analyze network path for the speed test result.
    /// For client tests, the path is from server (LocalIp) to client (DeviceHost).
    /// Retry logic is built into CalculatePathAsync.
    /// </summary>
    /// <param name="result">The speed test result to analyze</param>
    /// <param name="priorSnapshot">Optional wireless rate snapshot captured during the test</param>
    private async Task AnalyzePathAsync(Iperf3Result result, WirelessRateSnapshot? priorSnapshot = null)
    {
        try
        {
            _logger.LogDebug("Analyzing network path to {Client} from {Server}{Snapshot}",
                result.DeviceHost, result.LocalIp ?? "auto",
                priorSnapshot != null ? " (with snapshot)" : "");

            // When comparing with a snapshot, invalidate cache to get fresh "current" rates
            if (priorSnapshot != null)
            {
                _pathAnalyzer.InvalidateTopologyCache();
            }

            // Calculate path from server to client, using snapshot to pick max wireless rates
            var path = await _pathAnalyzer.CalculatePathAsync(
                result.DeviceHost,
                result.LocalIp,
                retryOnFailure: true,
                priorSnapshot);

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
                _logger.LogDebug("Path analysis complete: {Hops} hops", analysis.Path.Hops.Count);
            }
            else
            {
                _logger.LogDebug("Path analysis: path not found or invalid");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze path for {Client}", result.DeviceHost);
        }
    }

    /// <summary>
    /// Updates the result's WiFi rate fields with max values from path analysis.
    /// The hop rates already have max(snapshot, current) applied, so this syncs
    /// the result fields to match.
    /// </summary>
    private static void UpdateWifiRatesFromPathAnalysis(Iperf3Result result)
    {
        if (result.PathAnalysis?.Path?.Hops?.Count > 0)
        {
            var wirelessHop = result.PathAnalysis.Path.Hops.FirstOrDefault(h =>
                h.Type == HopType.WirelessClient);
            if (wirelessHop != null)
            {
                // IngressSpeedMbps = Tx (ToDevice), EgressSpeedMbps = Rx (FromDevice)
                var maxTxKbps = (long)(wirelessHop.IngressSpeedMbps * 1000);
                var maxRxKbps = (long)(wirelessHop.EgressSpeedMbps * 1000);

                // Only update if path analysis has higher values
                if (maxTxKbps > (result.WifiTxRateKbps ?? 0))
                    result.WifiTxRateKbps = maxTxKbps;
                if (maxRxKbps > (result.WifiRxRateKbps ?? 0))
                    result.WifiRxRateKbps = maxRxKbps;
            }
        }
    }

    /// <summary>
    /// Background task to enrich and analyze a speed test result after WiFi rates stabilize.
    /// Loads the result from DB, enriches with UniFi data, analyzes path, and saves.
    /// </summary>
    private async Task EnrichAndAnalyzeInBackgroundAsync(int resultId)
    {
        try
        {
            // Let WiFi link rates stabilize after the speed test
            await Task.Delay(TimeSpan.FromSeconds(2));

            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await db.Iperf3Results.FindAsync(resultId);
            if (result == null)
            {
                _logger.LogWarning("Result {Id} not found for background enrichment", resultId);
                return;
            }

            // Try to look up client info from UniFi
            await _connectionService.EnrichSpeedTestWithClientInfoAsync(result);

            // Get snapshot if available (captured during test by client callback)
            // For iperf3 client tests (ClientToServer), don't use snapshot here - preserve it for the merge path
            // The snapshot will be used when the second direction arrives and triggers a merge
            WirelessRateSnapshot? snapshot = null;
            if (result.Direction != SpeedTestDirection.ClientToServer)
            {
                snapshot = _snapshotService.GetSnapshot(result.DeviceHost);
            }

            // Perform path analysis (using snapshot to pick max wireless rates)
            await AnalyzePathAsync(result, snapshot);

            // Update result's WiFi rate fields with max values from path analysis
            UpdateWifiRatesFromPathAnalysis(result);

            // Clean up snapshot after use (iperf3 client snapshots cleaned up in merge path or auto-expire)
            if (snapshot != null)
                _snapshotService.RemoveSnapshot(result.DeviceHost);

            await db.SaveChangesAsync();

            _logger.LogDebug("Background enrichment complete for result {Id}: {DeviceName}",
                resultId, result.DeviceName ?? result.DeviceHost);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enrich result {Id} in background", resultId);
        }
    }
}
