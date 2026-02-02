using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing client-initiated speed tests (browser-based and iperf3 clients).
/// Uses the unified Iperf3Result table with Direction field to distinguish test types.
/// Client-initiated tests are saved to the default site (first enabled site).
/// </summary>
public class ClientSpeedTestService
{
    private readonly ILogger<ClientSpeedTestService> _logger;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly ITopologySnapshotService _snapshotService;
    private readonly IConfiguration _configuration;
    private readonly ISiteRepository _siteRepository;
    private readonly IMemoryCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    // Cache the default site ID to avoid repeated DB lookups
    private int? _cachedDefaultSiteId;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    // Site-specific path analyzers (keyed by siteId)
    private readonly ConcurrentDictionary<int, INetworkPathAnalyzer> _pathAnalyzers = new();

    public ClientSpeedTestService(
        ILogger<ClientSpeedTestService> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        ITopologySnapshotService snapshotService,
        IConfiguration configuration,
        ISiteRepository siteRepository,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _snapshotService = snapshotService;
        _configuration = configuration;
        _siteRepository = siteRepository;
        _cache = cache;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets or creates a site-specific path analyzer.
    /// Each site needs its own analyzer to use the correct UniFi connection.
    /// </summary>
    private INetworkPathAnalyzer GetPathAnalyzer(int siteId)
    {
        return _pathAnalyzers.GetOrAdd(siteId, id =>
        {
            var clientProvider = new SiteSpecificClientProvider(_connectionService, id);
            return new NetworkPathAnalyzer(clientProvider, _cache, _loggerFactory);
        });
    }

    /// <summary>
    /// Gets the default site ID for client-initiated speed tests.
    /// Returns the first enabled site, or 1001 as a fallback.
    /// </summary>
    private async Task<int> GetDefaultSiteIdAsync()
    {
        // Return cached value if still valid
        if (_cachedDefaultSiteId.HasValue && DateTime.UtcNow < _cacheExpiry)
            return _cachedDefaultSiteId.Value;

        var sites = await _siteRepository.GetAllSitesAsync();
        var defaultSite = sites.FirstOrDefault(s => s.Enabled);
        _cachedDefaultSiteId = defaultSite?.Id ?? 1001;
        _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);

        return _cachedDefaultSiteId.Value;
    }

    /// <summary>
    /// Record a speed test result from OpenSpeedTest browser client.
    /// </summary>
    public async Task<Iperf3Result> RecordOpenSpeedTestResultAsync(
        int siteId,
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
            SiteId = siteId,
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
        _ = Task.Run(async () => await EnrichAndAnalyzeInBackgroundAsync(siteId, resultId));

        return result;
    }

    /// <summary>
    /// Record a speed test result from an iperf3 client.
    /// Merges with recent result from same client if one direction is missing.
    /// </summary>
    /// <param name="clientIp">Client IP address</param>
    /// <param name="downloadBitsPerSecond">Download speed (server receiving from client)</param>
    /// <param name="uploadBitsPerSecond">Upload speed (server sending to client)</param>
    /// <param name="downloadBytes">Total bytes downloaded</param>
    /// <param name="uploadBytes">Total bytes uploaded</param>
    /// <param name="downloadRetransmits">TCP retransmits for download</param>
    /// <param name="uploadRetransmits">TCP retransmits for upload</param>
    /// <param name="durationSeconds">Test duration in seconds</param>
    /// <param name="parallelStreams">Number of parallel streams</param>
    /// <param name="rawJson">Raw iperf3 JSON output</param>
    /// <param name="serverLocalIp">Server's local IP from iperf3</param>
    /// <param name="requestedSiteId">Site ID from iperf3 --extra-data (null = use default site)</param>
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
        string? serverLocalIp = null,
        int? requestedSiteId = null)
    {
        var now = DateTime.UtcNow;
        // Use the actual server IP from iperf3, fall back to HOST_IP config
        var serverIp = serverLocalIp ?? _configuration["HOST_IP"];

        // Use requested site ID if provided and valid, otherwise fall back to default
        var siteId = requestedSiteId > 0 ? requestedSiteId.Value : await GetDefaultSiteIdAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Check for recent result from same client that we can merge with
        // (within 60 seconds, one has download but no upload, or vice versa)
        var mergeWindow = now.AddSeconds(-60);
        var recentResult = await db.Iperf3Results
            .Where(r => r.Direction == SpeedTestDirection.ClientToServer
                     && r.DeviceHost == clientIp
                     && r.SiteId == siteId
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

            // Re-analyze path with updated bidirectional data (using site-specific connection and snapshot for max rates)
            await AnalyzePathAsync(siteId, recentResult, snapshot);

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
            SiteId = siteId,
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
        _ = Task.Run(async () => await EnrichAndAnalyzeInBackgroundAsync(siteId, resultId));

        return result;
    }

    /// <summary>
    /// Get recent client speed test results (ClientToServer and BrowserToServer directions).
    /// Retries path analysis for results missing valid paths.
    /// </summary>
    /// <param name="siteId">Site ID to filter results</param>
    /// <param name="count">Maximum number of results (0 = no limit)</param>
    /// <param name="hours">Filter to results within the last N hours (0 = all time)</param>
    public async Task<List<Iperf3Result>> GetResultsAsync(int siteId, int count = 50, int hours = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Iperf3Results
            .Where(r => r.SiteId == siteId)
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
            _logger.LogInformation("Retrying path analysis for site {SiteId}: {Count} results without valid paths", siteId, needsRetry.Count);
            foreach (var result in needsRetry)
            {
                // Use result's own SiteId to ensure correct site context
                await AnalyzePathAsync(result.SiteId, result);
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
    /// Analyze network path for the speed test result using the site-specific UniFi connection.
    /// For client tests, the path is from server (LocalIp) to client (DeviceHost).
    /// </summary>
    /// <param name="siteId">The site identifier</param>
    /// <param name="result">The speed test result to analyze</param>
    /// <param name="priorSnapshot">Optional wireless rate snapshot captured during the test</param>
    private async Task AnalyzePathAsync(int siteId, Iperf3Result result, WirelessRateSnapshot? priorSnapshot = null)
    {
        try
        {
            _logger.LogDebug("Analyzing network path for site {SiteId} to {Client} from {Server}{Snapshot}",
                siteId, result.DeviceHost, result.LocalIp ?? "auto",
                priorSnapshot != null ? " (with snapshot)" : "");

            // Get the site-specific path analyzer
            var pathAnalyzer = GetPathAnalyzer(siteId);

            // When comparing with a snapshot, invalidate cache to get fresh "current" rates
            if (priorSnapshot != null)
            {
                pathAnalyzer.InvalidateTopologyCache();
            }

            // Calculate path from server to client, using snapshot to pick max wireless rates
            var path = await pathAnalyzer.CalculatePathAsync(
                result.DeviceHost,
                result.LocalIp,
                retryOnFailure: true,
                priorSnapshot);

            // Analyze speed test against the path
            var analysis = pathAnalyzer.AnalyzeSpeedTest(
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
            _logger.LogWarning(ex, "Failed to analyze path for site {SiteId}, {Client}", siteId, result.DeviceHost);
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
    private async Task EnrichAndAnalyzeInBackgroundAsync(int siteId, int resultId)
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

            // Try to look up client info from UniFi (site-specific)
            await _connectionService.EnrichSpeedTestWithClientInfoAsync(siteId, result);

            // Get snapshot if available (captured during test by client callback)
            // For iperf3 client tests (ClientToServer), don't use snapshot here - preserve it for the merge path
            // The snapshot will be used when the second direction arrives and triggers a merge
            WirelessRateSnapshot? snapshot = null;
            if (result.Direction != SpeedTestDirection.ClientToServer)
            {
                snapshot = _snapshotService.GetSnapshot(result.DeviceHost);
            }

            // Perform path analysis (site-specific, using snapshot to pick max wireless rates)
            await AnalyzePathAsync(siteId, result, snapshot);

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

/// <summary>
/// Site-specific IUniFiClientProvider that wraps a single site's connection.
/// </summary>
internal class SiteSpecificClientProvider : IUniFiClientProvider
{
    private readonly UniFiConnectionService _connectionService;
    private readonly int _siteId;

    public SiteSpecificClientProvider(UniFiConnectionService connectionService, int siteId)
    {
        _connectionService = connectionService;
        _siteId = siteId;
    }

    public bool IsConnected => _connectionService.IsConnected(_siteId);
    public UniFiApiClient? Client => _connectionService.GetClient(_siteId);
}
