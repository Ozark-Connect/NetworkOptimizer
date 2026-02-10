using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running WAN speed tests via Cloudflare's speed test infrastructure.
/// Uses HTTP GET/POST to speed.cloudflare.com with Server-Timing header parsing.
/// Employs concurrent connections (like the Cloudflare browser test and cloudflare-speed-cli)
/// to saturate the link for accurate measurement.
/// </summary>
public partial class CloudflareSpeedTestService
{
    private const string BaseUrl = "https://speed.cloudflare.com";
    private const string DownloadPath = "__down?bytes=";
    private const string UploadPath = "__up";

    // Concurrency and duration settings (matching cloudflare-speed-cli defaults)
    private const int Concurrency = 6;
    private static readonly TimeSpan DownloadDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadDuration = TimeSpan.FromSeconds(10);
    private const int DownloadBytesPerRequest = 25_000_000; // 25 MB per request
    private const int UploadBytesPerRequest = 1_000_000;    // 1 MB per request (small so many complete per duration)

    private readonly ILogger<CloudflareSpeedTestService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly IConfiguration _configuration;

    // Observable test state (polled by UI components)
    private readonly object _lock = new();
    private bool _isRunning;
    private string _currentPhase = "";
    private int _currentPercent;
    private string? _currentStatus;
    private Iperf3Result? _lastCompletedResult;

    /// <summary>Whether a WAN speed test is currently running.</summary>
    public bool IsRunning { get { lock (_lock) return _isRunning; } }

    /// <summary>Current test progress snapshot for UI polling.</summary>
    public (string Phase, int Percent, string? Status) CurrentProgress
    {
        get { lock (_lock) return (_currentPhase, _currentPercent, _currentStatus); }
    }

    /// <summary>
    /// Last completed result from the current session.
    /// UI can poll this to detect completion after navigating away and back.
    /// </summary>
    public Iperf3Result? LastCompletedResult
    {
        get { lock (_lock) return _lastCompletedResult; }
    }

    /// <summary>
    /// Metadata from the most recent test (set early in the test lifecycle).
    /// </summary>
    public CloudflareMetadata? LastMetadata
    {
        get { lock (_lock) return _lastMetadata; }
    }
    private CloudflareMetadata? _lastMetadata;

    public CloudflareSpeedTestService(
        ILogger<CloudflareSpeedTestService> logger,
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        INetworkPathAnalyzer pathAnalyzer,
        IConfiguration configuration)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _pathAnalyzer = pathAnalyzer;
        _configuration = configuration;
    }

    /// <summary>
    /// Cloudflare edge metadata from response headers.
    /// </summary>
    public record CloudflareMetadata(string Ip, string City, string Country, string Asn, string Colo);

    /// <summary>
    /// Run a full Cloudflare WAN speed test with progress reporting.
    /// Uses 6 concurrent connections per direction, similar to cloudflare-speed-cli.
    /// Test state is tracked on the service so UI components can navigate away and
    /// poll CurrentProgress / LastCompletedResult when they return.
    /// </summary>
    public async Task<Iperf3Result?> RunTestAsync(
        Action<(string Phase, int Percent, string? Status)>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("WAN speed test already in progress");
                return null;
            }
            _isRunning = true;
            _lastCompletedResult = null;
        }

        try
        {
            _logger.LogInformation("Starting Cloudflare WAN speed test ({Concurrency} concurrent connections)", Concurrency);

            // Wrap progress to always update service-level state (for navigate-away polling)
            void Report(string phase, int percent, string? status)
            {
                lock (_lock) { _currentPhase = phase; _currentPercent = percent; _currentStatus = status; }
                onProgress?.Invoke((phase, percent, status));
            }

            Report("Connecting", 0, null);

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetworkOptimizer/1.0");

            // Phase 1: Metadata (0-5%)
            Report("Metadata", 2, "Fetching edge info...");
            var metadata = await FetchMetadataAsync(client, cancellationToken);
            lock (_lock) _lastMetadata = metadata;
            var edgeInfo = $"{metadata.Colo} - {metadata.City}, {metadata.Country}";
            _logger.LogInformation("Connected to Cloudflare edge: {Edge} (IP: {Ip}, ASN: {Asn})",
                edgeInfo, metadata.Ip, metadata.Asn);
            Report("Metadata", 5, edgeInfo);

            // Phase 2: Latency (5-15%)
            Report("Latency", 7, "Measuring latency...");
            var (latencyMs, jitterMs) = await MeasureLatencyAsync(client, cancellationToken);
            _logger.LogInformation("Latency: {Latency:F1} ms, Jitter: {Jitter:F1} ms", latencyMs, jitterMs);
            Report("Latency", 15, $"{latencyMs:F1} ms / {jitterMs:F1} ms jitter");

            // Phase 3: Download (15-55%) - concurrent connections + latency probes
            Report("Testing download", 16, null);
            var (downloadBps, downloadBytes, dlLatencyMs, dlJitterMs) = await MeasureThroughputAsync(
                isUpload: false,
                DownloadDuration,
                DownloadBytesPerRequest,
                pct => Report("Testing download", 15 + (int)(pct * 40), null),
                cancellationToken);
            var downloadMbps = downloadBps / 1_000_000.0;
            _logger.LogInformation("Download: {Speed:F1} Mbps ({Bytes} bytes, {Workers} workers), loaded latency: {Latency:F1} ms",
                downloadMbps, downloadBytes, Concurrency, dlLatencyMs);
            Report("Download complete", 55, $"{downloadMbps:F1} Mbps");

            // Phase 4: Upload (55-95%) - concurrent connections + latency probes
            Report("Testing upload", 56, null);
            var (uploadBps, uploadBytes, ulLatencyMs, ulJitterMs) = await MeasureThroughputAsync(
                isUpload: true,
                UploadDuration,
                UploadBytesPerRequest,
                pct => Report("Testing upload", 55 + (int)(pct * 40), null),
                cancellationToken);
            var uploadMbps = uploadBps / 1_000_000.0;
            _logger.LogInformation("Upload: {Speed:F1} Mbps ({Bytes} bytes, {Workers} workers), loaded latency: {Latency:F1} ms",
                uploadMbps, uploadBytes, Concurrency, ulLatencyMs);
            Report("Upload complete", 95, $"{uploadMbps:F1} Mbps");

            // Phase 5: Save result (95-100%)
            Report("Saving", 96, "Saving results...");

            var serverIp = _configuration["HOST_IP"];

            // Store from WAN perspective:
            // DownloadBitsPerSecond = CF download (data received from internet)
            // UploadBitsPerSecond = CF upload (data sent to internet)
            var result = new Iperf3Result
            {
                Direction = SpeedTestDirection.CloudflareWan,
                DeviceHost = "speed.cloudflare.com",
                DeviceName = edgeInfo,
                DeviceType = "WAN",
                LocalIp = serverIp,
                DownloadBitsPerSecond = downloadBps,
                UploadBitsPerSecond = uploadBps,
                DownloadBytes = downloadBytes,
                UploadBytes = uploadBytes,
                PingMs = latencyMs,
                JitterMs = jitterMs,
                DownloadLatencyMs = dlLatencyMs > 0 ? dlLatencyMs : null,
                DownloadJitterMs = dlJitterMs > 0 ? dlJitterMs : null,
                UploadLatencyMs = ulLatencyMs > 0 ? ulLatencyMs : null,
                UploadJitterMs = ulJitterMs > 0 ? ulJitterMs : null,
                TestTime = DateTime.UtcNow,
                Success = true,
                ParallelStreams = Concurrency,
                DurationSeconds = (int)(DownloadDuration + UploadDuration).TotalSeconds,
            };

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.Iperf3Results.Add(result);
            await db.SaveChangesAsync(cancellationToken);
            var resultId = result.Id;

            _logger.LogInformation(
                "WAN speed test complete: Down {Download:F1} Mbps, Up {Upload:F1} Mbps, Latency {Latency:F1} ms",
                downloadMbps, uploadMbps, latencyMs);

            Report("Complete", 100, $"Down: {downloadMbps:F1} / Up: {uploadMbps:F1} Mbps");
            lock (_lock) _lastCompletedResult = result;

            // Trigger background path analysis
            _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(resultId), CancellationToken.None);

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WAN speed test cancelled");
            lock (_lock) { _currentPhase = "Cancelled"; _currentPercent = 0; _currentStatus = "Test cancelled"; }
            onProgress?.Invoke(("Cancelled", 0, "Test cancelled"));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WAN speed test failed");
            lock (_lock) { _currentPhase = "Error"; _currentPercent = 0; _currentStatus = ex.Message; }
            onProgress?.Invoke(("Error", 0, ex.Message));

            // Save failed result
            try
            {
                var failedResult = new Iperf3Result
                {
                    Direction = SpeedTestDirection.CloudflareWan,
                    DeviceHost = "speed.cloudflare.com",
                    DeviceName = "Cloudflare",
                    DeviceType = "WAN",
                    TestTime = DateTime.UtcNow,
                    Success = false,
                    ErrorMessage = ex.Message,
                };
                await using var db = await _dbFactory.CreateDbContextAsync();
                db.Iperf3Results.Add(failedResult);
                await db.SaveChangesAsync();
                return failedResult;
            }
            catch (Exception saveEx)
            {
                _logger.LogWarning(saveEx, "Failed to save error result");
                return null;
            }
        }
        finally
        {
            lock (_lock) _isRunning = false;
        }
    }

    /// <summary>
    /// Get recent WAN speed test results.
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Iperf3Results
            .Where(r => r.Direction == SpeedTestDirection.CloudflareWan);

        if (hours > 0)
        {
            var cutoff = DateTime.UtcNow.AddHours(-hours);
            query = query.Where(r => r.TestTime >= cutoff);
        }

        query = query.OrderByDescending(r => r.TestTime);

        if (count > 0)
            query = query.Take(count);

        return await query.ToListAsync();
    }

    /// <summary>
    /// Delete a WAN speed test result by ID.
    /// </summary>
    public async Task<bool> DeleteResultAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || result.Direction != SpeedTestDirection.CloudflareWan)
            return false;

        db.Iperf3Results.Remove(result);
        await db.SaveChangesAsync();
        _logger.LogInformation("Deleted WAN speed test result {Id}", id);
        return true;
    }

    /// <summary>
    /// Updates the notes for a WAN speed test result.
    /// </summary>
    public async Task<bool> UpdateNotesAsync(int id, string? notes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || result.Direction != SpeedTestDirection.CloudflareWan)
            return false;

        result.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync();
        return true;
    }

    private static async Task<CloudflareMetadata> FetchMetadataAsync(HttpClient client, CancellationToken ct)
    {
        // Use /cdn-cgi/trace which reliably returns key=value metadata
        // (cf-meta-* headers are no longer consistently populated)
        var url = $"{BaseUrl}/cdn-cgi/trace";
        var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = line.IndexOf('=');
            if (eqIdx > 0)
                data[line[..eqIdx].Trim()] = line[(eqIdx + 1)..].Trim();
        }

        var colo = data.GetValueOrDefault("colo") ?? "";
        var city = ColoToCityName(colo);
        var country = data.GetValueOrDefault("loc") ?? "";

        return new CloudflareMetadata(
            Ip: data.GetValueOrDefault("ip") ?? "",
            City: city,
            Country: country,
            Asn: "", // Not available from trace endpoint
            Colo: colo);
    }

    // Lazy-loaded IATA colo code → city name lookup from bundled JSON
    private static Dictionary<string, string>? _coloLookup;
    private static readonly object _coloLock = new();

    private static string ColoToCityName(string colo)
    {
        if (string.IsNullOrEmpty(colo)) return "";

        lock (_coloLock)
        {
            if (_coloLookup == null)
            {
                _coloLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    // Load from bundled wwwroot file (deployed as embedded content)
                    var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "cloudflare-colos.json");
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.TryGetProperty("city", out var city))
                                _coloLookup[prop.Name] = city.GetString() ?? prop.Name;
                        }
                    }
                }
                catch
                {
                    // Graceful fallback - just show the colo code
                }
            }
        }

        return _coloLookup.TryGetValue(colo, out var cityName) ? cityName : colo;
    }

    /// <summary>
    /// Measure latency using 20 zero-byte downloads, parsing Server-Timing headers.
    /// Returns (medianLatencyMs, jitterMs).
    /// </summary>
    private static async Task<(double LatencyMs, double JitterMs)> MeasureLatencyAsync(
        HttpClient client, CancellationToken ct)
    {
        var latencies = new List<double>();
        var url = $"{BaseUrl}/{DownloadPath}0";

        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync(url, ct);
            sw.Stop();

            var serverMs = ParseServerTiming(response);
            var latency = sw.Elapsed.TotalMilliseconds - serverMs;
            if (latency < 0) latency = 0;
            latencies.Add(latency);
        }

        latencies.Sort();

        // Median
        var count = latencies.Count;
        var median = count % 2 == 0
            ? (latencies[count / 2 - 1] + latencies[count / 2]) / 2.0
            : latencies[count / 2];

        // Jitter: average of consecutive differences
        var jitter = 0.0;
        if (latencies.Count >= 2)
        {
            var diffs = new List<double>();
            for (int i = 1; i < latencies.Count; i++)
                diffs.Add(Math.Abs(latencies[i] - latencies[i - 1]));
            jitter = diffs.Average();
        }

        return (Math.Round(median, 1), Math.Round(jitter, 1));
    }

    /// <summary>
    /// Measure throughput using concurrent workers for a fixed duration, with concurrent
    /// latency probes to measure loaded latency (bufferbloat).
    /// Each worker continuously sends/receives data. Aggregate throughput is measured
    /// by sampling total bytes every 200ms and computing mean of per-interval Mbps.
    /// Skips the first 20% of samples (warmup) for accurate steady-state measurement.
    /// </summary>
    private async Task<(double BitsPerSecond, long TotalBytes, double LoadedLatencyMs, double LoadedJitterMs)> MeasureThroughputAsync(
        bool isUpload,
        TimeSpan duration,
        int bytesPerRequest,
        Action<double> onProgress,
        CancellationToken ct)
    {
        var stop = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        long totalBytes = 0;

        // Loaded latency samples (collected by concurrent probe task)
        var loadedLatencies = new System.Collections.Concurrent.ConcurrentBag<double>();

        // Launch concurrent workers
        var tasks = new Task[Concurrency];
        for (int w = 0; w < Concurrency; w++)
        {
            tasks[w] = Task.Run(async () =>
            {
                using var workerClient = _httpClientFactory.CreateClient();
                workerClient.Timeout = TimeSpan.FromSeconds(60);
                workerClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetworkOptimizer/1.0");

                while (!linked.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (isUpload)
                        {
                            var url = $"{BaseUrl}/{UploadPath}";
                            var payload = new byte[bytesPerRequest];
                            using var content = new ByteArrayContent(payload);
                            await workerClient.PostAsync(url, content, linked.Token);
                            Interlocked.Add(ref totalBytes, bytesPerRequest);
                        }
                        else
                        {
                            // Stream download to count bytes incrementally as they arrive,
                            // not after the full response completes (which may exceed duration)
                            var url = $"{BaseUrl}/{DownloadPath}{bytesPerRequest}";
                            using var response = await workerClient.GetAsync(url,
                                HttpCompletionOption.ResponseHeadersRead, linked.Token);
                            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                            var buffer = new byte[81920]; // 80 KB read buffer
                            int bytesRead;
                            while ((bytesRead = await stream.ReadAsync(buffer, linked.Token)) > 0)
                            {
                                Interlocked.Add(ref totalBytes, bytesRead);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // Request failed - continue with next request
                    }
                }
            }, linked.Token);
        }

        // Launch latency probe task - runs 0-byte GETs every 500ms during throughput test
        var probeTask = Task.Run(async () =>
        {
            using var probeClient = _httpClientFactory.CreateClient();
            probeClient.Timeout = TimeSpan.FromSeconds(10);
            probeClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetworkOptimizer/1.0");
            var probeUrl = $"{BaseUrl}/{DownloadPath}0";

            while (!linked.Token.IsCancellationRequested)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    var response = await probeClient.GetAsync(probeUrl, linked.Token);
                    sw.Stop();

                    var serverMs = ParseServerTiming(response);
                    var latency = sw.Elapsed.TotalMilliseconds - serverMs;
                    if (latency > 0)
                        loadedLatencies.Add(latency);

                    await Task.Delay(500, linked.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { /* Probe failed, skip */ }
            }
        }, linked.Token);

        // Measure aggregate throughput over the test duration
        var startTime = Stopwatch.StartNew();
        var mbpsSamples = new List<double>();
        long lastBytes = 0;
        var lastTime = startTime.Elapsed;

        while (startTime.Elapsed < duration)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct);

            var now = startTime.Elapsed;
            var currentBytes = Interlocked.Read(ref totalBytes);
            var intervalBytes = currentBytes - lastBytes;
            var intervalSeconds = (now - lastTime).TotalSeconds;

            if (intervalSeconds > 0.01)
            {
                var mbps = (intervalBytes * 8.0 / 1_000_000.0) / intervalSeconds;
                mbpsSamples.Add(mbps);
            }

            lastBytes = currentBytes;
            lastTime = now;
            onProgress(startTime.Elapsed / duration);
        }

        // Signal workers to stop
        stop.Cancel();

        // Wait for workers with timeout
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch { /* Workers may throw on cancellation */ }

        // Wait for probe task
        try { await probeTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch { /* Probe may throw on cancellation */ }

        stop.Dispose();
        linked.Dispose();

        // Compute mean Mbps from steady-state samples (skip first 20% warmup)
        var finalBytes = Interlocked.Read(ref totalBytes);
        if (mbpsSamples.Count == 0)
            return (0, finalBytes, 0, 0);

        var skipCount = (int)(mbpsSamples.Count * 0.20);
        var steadySamples = mbpsSamples.Skip(skipCount).ToList();
        if (steadySamples.Count == 0)
            steadySamples = mbpsSamples;

        var meanMbps = steadySamples.Average();
        var bitsPerSecond = meanMbps * 1_000_000.0;

        // Compute loaded latency median and jitter from probe samples
        var sortedLatencies = loadedLatencies.OrderBy(l => l).ToList();
        double loadedLatencyMs = 0, loadedJitterMs = 0;
        if (sortedLatencies.Count > 0)
        {
            // Median
            var count = sortedLatencies.Count;
            loadedLatencyMs = count % 2 == 0
                ? (sortedLatencies[count / 2 - 1] + sortedLatencies[count / 2]) / 2.0
                : sortedLatencies[count / 2];

            // Jitter: average of consecutive differences
            if (sortedLatencies.Count >= 2)
            {
                var diffs = new List<double>();
                for (int i = 1; i < sortedLatencies.Count; i++)
                    diffs.Add(Math.Abs(sortedLatencies[i] - sortedLatencies[i - 1]));
                loadedJitterMs = diffs.Average();
            }

            loadedLatencyMs = Math.Round(loadedLatencyMs, 1);
            loadedJitterMs = Math.Round(loadedJitterMs, 1);
        }

        return (bitsPerSecond, finalBytes, loadedLatencyMs, loadedJitterMs);
    }

    private static double ParseServerTiming(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Server-Timing", out var values))
            return 0;
        var header = values.FirstOrDefault() ?? "";
        var match = ServerTimingRegex().Match(header);
        return match.Success && double.TryParse(match.Groups[1].Value, out var ms) ? ms : 0;
    }

    [GeneratedRegex(@"cfRequestDuration;dur=([\d.]+)")]
    private static partial Regex ServerTimingRegex();

    /// <summary>
    /// Background path analysis after test completes.
    /// </summary>
    private async Task AnalyzePathInBackgroundAsync(int resultId)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await db.Iperf3Results.FindAsync(resultId);
            if (result == null) return;

            var path = await _pathAnalyzer.CalculatePathAsync(
                result.DeviceHost, result.LocalIp, retryOnFailure: true);

            var analysis = _pathAnalyzer.AnalyzeSpeedTest(
                path,
                result.DownloadMbps,
                result.UploadMbps,
                result.DownloadRetransmits,
                result.UploadRetransmits,
                result.DownloadBytes,
                result.UploadBytes);

            result.PathAnalysis = analysis;
            await db.SaveChangesAsync();

            _logger.LogDebug("WAN speed test path analysis complete for result {Id}", resultId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze path for WAN speed test result {Id}", resultId);
        }
    }
}
