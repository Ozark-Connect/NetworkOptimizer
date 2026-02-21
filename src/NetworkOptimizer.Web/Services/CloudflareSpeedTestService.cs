using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
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
public partial class CloudflareSpeedTestService : WanSpeedTestServiceBase
{
    private const string BaseUrl = "https://speed.cloudflare.com";
    private const string DownloadPath = "__down?bytes=";
    private const string UploadPath = "__up";

    // Concurrency and duration settings
    private const int Concurrency = 8;
    private static readonly TimeSpan DownloadDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadDuration = TimeSpan.FromSeconds(10);
    private const int DownloadBytesPerRequest = 10_000_000; // 10 MB per request (matches cloudflare-speed-cli)
    private const int MinDownloadBytesPerRequest = 100_000; // Floor for adaptive chunk reduction on 429
    private const int UploadBytesPerRequest = 5_000_000;    // 5 MB per request (matches cloudflare-speed-cli)

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    protected override SpeedTestDirection Direction => SpeedTestDirection.CloudflareWan;

    /// <summary>
    /// Cloudflare edge metadata from response headers.
    /// </summary>
    public record CloudflareMetadata(string Ip, string City, string Country, string Asn, string Colo);

    public CloudflareSpeedTestService(
        ILogger<CloudflareSpeedTestService> logger,
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        INetworkPathAnalyzer pathAnalyzer,
        IConfiguration configuration,
        Iperf3ServerService iperf3ServerService,
        IAlertEventBus? alertEventBus = null)
        : base(dbFactory, pathAnalyzer, logger, iperf3ServerService, alertEventBus)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task<Iperf3Result?> RunTestCoreAsync(
        Action<string, int, string?> report,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting Cloudflare WAN speed test ({Concurrency} concurrent connections)", Concurrency);

        report("Connecting", 0, null);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetworkOptimizer/1.0");

        // Phase 1: Metadata (0-5%)
        report("Metadata", 2, "Fetching edge info...");
        var metadata = await FetchMetadataAsync(client, cancellationToken);
        SetMetadata(new WanTestMetadata(metadata.Colo, $"{metadata.City}, {metadata.Country}", metadata.Ip));
        var edgeInfo = $"{metadata.Colo} - {metadata.City}, {metadata.Country}";
        Logger.LogInformation("Connected to Cloudflare edge: {Edge} (IP: {Ip}, ASN: {Asn})",
            edgeInfo, metadata.Ip, metadata.Asn);
        report("Metadata", 5, edgeInfo);

        // Phase 2: Latency (5-15%)
        report("Testing latency", 7, null);
        var (latencyMs, jitterMs) = await MeasureLatencyAsync(client, cancellationToken);
        Logger.LogInformation("Latency: {Latency:F1} ms, Jitter: {Jitter:F1} ms", latencyMs, jitterMs);
        report("Testing latency", 15, $"Latency: {latencyMs:F1} ms / {jitterMs:F1} ms jitter");

        // Phase 3: Download (15-55%) - concurrent connections + latency probes
        report("Testing download", 16, null);
        var (downloadBps, downloadBytes, dlLatencyMs, dlJitterMs) = await MeasureThroughputAsync(
            isUpload: false,
            DownloadDuration,
            DownloadBytesPerRequest,
            pct => report("Testing download", 15 + (int)(pct * 40), null),
            cancellationToken);
        var downloadMbps = downloadBps / 1_000_000.0;
        Logger.LogInformation("Download: {Speed:F1} Mbps ({Bytes} bytes, {Workers} workers), loaded latency: {Latency:F1} ms",
            downloadMbps, downloadBytes, Concurrency, dlLatencyMs);
        report("Download complete", 55, $"Down: {downloadMbps:F1} Mbps");

        // Phase 4: Upload (55-95%) - concurrent connections + latency probes
        report("Testing upload", 56, null);
        var (uploadBps, uploadBytes, ulLatencyMs, ulJitterMs) = await MeasureThroughputAsync(
            isUpload: true,
            UploadDuration,
            UploadBytesPerRequest,
            pct => report("Testing upload", 55 + (int)(pct * 40), null),
            cancellationToken);
        var uploadMbps = uploadBps / 1_000_000.0;
        Logger.LogInformation("Upload: {Speed:F1} Mbps ({Bytes} bytes, {Workers} workers), loaded latency: {Latency:F1} ms",
            uploadMbps, uploadBytes, Concurrency, ulLatencyMs);
        report("Upload complete", 95, null);

        // Phase 5: Build result (95-100%)
        report("Saving", 96, null);

        var serverIp = _configuration["HOST_IP"];

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
            DurationSeconds = (int)DownloadDuration.TotalSeconds,
        };

        // Identify WAN connection from Cloudflare-reported IP
        try
        {
            var (wanGroup, wanName) = await PathAnalyzer.IdentifyWanConnectionAsync(
                metadata.Ip, downloadMbps, uploadMbps, cancellationToken);
            result.WanNetworkGroup = wanGroup;
            result.WanName = wanName;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not identify WAN connection for IP {Ip}", metadata.Ip);
        }

        Logger.LogInformation(
            "WAN speed test complete: Down {Download:F1} Mbps, Up {Upload:F1} Mbps, Latency {Latency:F1} ms",
            downloadMbps, uploadMbps, latencyMs);

        report("Complete", 100, $"Down: {downloadMbps:F1} / Up: {uploadMbps:F1} Mbps");

        return result;
    }

    protected override Iperf3Result CreateFailedResult(string errorMessage) => new()
    {
        Direction = SpeedTestDirection.CloudflareWan,
        DeviceHost = "speed.cloudflare.com",
        DeviceName = "Cloudflare",
        DeviceType = "WAN",
        TestTime = DateTime.UtcNow,
        Success = false,
        ErrorMessage = errorMessage,
    };

    private static async Task<CloudflareMetadata> FetchMetadataAsync(HttpClient client, CancellationToken ct)
    {
        var url = $"{BaseUrl}/cdn-cgi/trace";
        using var response = await client.GetAsync(url, ct);
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
            Asn: "",
            Colo: colo);
    }

    // Lazy-loaded IATA colo code -> city name lookup from bundled JSON
    private static Dictionary<string, string>? _coloLookup;
    private static readonly object _coloLock = new();

    /// <summary>
    /// Look up city name from Cloudflare colo (IATA airport) code.
    /// </summary>
    public static string GetCityName(string colo) => ColoToCityName(colo);

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
    /// </summary>
    private static async Task<(double LatencyMs, double JitterMs)> MeasureLatencyAsync(
        HttpClient client, CancellationToken ct)
    {
        var latencies = new List<double>();
        var url = $"{BaseUrl}/{DownloadPath}0";

        for (int i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync(url, ct);
            sw.Stop();

            var serverMs = ParseServerTiming(response);
            var latency = sw.Elapsed.TotalMilliseconds - serverMs;
            if (latency < 0) latency = 0;
            latencies.Add(latency);
        }

        latencies.Sort();

        var count = latencies.Count;
        var median = count % 2 == 0
            ? (latencies[count / 2 - 1] + latencies[count / 2]) / 2.0
            : latencies[count / 2];

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
    /// </summary>
    private async Task<(double BitsPerSecond, long TotalBytes, double LoadedLatencyMs, double LoadedJitterMs)> MeasureThroughputAsync(
        bool isUpload,
        TimeSpan duration,
        int bytesPerRequest,
        Action<double> onProgress,
        CancellationToken ct)
    {
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        long totalBytes = 0;
        long errorCount = 0;
        long requestCount = 0;

        var loadedLatencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var uploadPayload = isUpload ? new byte[bytesPerRequest] : null;
        var direction = isUpload ? "upload" : "download";

        var tasks = new Task[Concurrency];
        for (int w = 0; w < Concurrency; w++)
        {
            tasks[w] = Task.Run(async () =>
            {
                using var workerClient = _httpClientFactory.CreateClient();
                workerClient.Timeout = TimeSpan.FromSeconds(60);
                workerClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "NetworkOptimizer/1.0");
                var readBuffer = isUpload ? null : new byte[81920];
                var workerChunkSize = bytesPerRequest;

                while (!linked.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (isUpload)
                        {
                            var url = $"{BaseUrl}/{UploadPath}";
                            using var content = new ProgressContent(uploadPayload!, bytesWritten =>
                                Interlocked.Add(ref totalBytes, bytesWritten));
                            using var uploadResponse = await workerClient.PostAsync(url, content, linked.Token);
                            Interlocked.Increment(ref requestCount);
                            await uploadResponse.Content.CopyToAsync(Stream.Null, linked.Token);
                            if (!uploadResponse.IsSuccessStatusCode)
                            {
                                Interlocked.Increment(ref errorCount);
                                Logger.LogDebug("WAN {Direction} worker got HTTP {Status}",
                                    direction, (int)uploadResponse.StatusCode);
                                await Task.Delay(100, linked.Token);
                                continue;
                            }
                        }
                        else
                        {
                            var url = $"{BaseUrl}/{DownloadPath}{workerChunkSize}";
                            using var response = await workerClient.GetAsync(url,
                                HttpCompletionOption.ResponseHeadersRead, linked.Token);
                            Interlocked.Increment(ref requestCount);
                            if (!response.IsSuccessStatusCode)
                            {
                                Interlocked.Increment(ref errorCount);
                                if ((int)response.StatusCode == 429)
                                {
                                    var next = Math.Max(workerChunkSize / 2, MinDownloadBytesPerRequest);
                                    if (next < workerChunkSize)
                                    {
                                        Logger.LogDebug("WAN download worker got 429, reducing chunk from {Old} to {New} bytes",
                                            workerChunkSize, next);
                                        workerChunkSize = next;
                                    }
                                }
                                await Task.Delay(100, linked.Token);
                                continue;
                            }
                            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                            int bytesRead;
                            while ((bytesRead = await stream.ReadAsync(readBuffer!, linked.Token)) > 0)
                            {
                                Interlocked.Add(ref totalBytes, bytesRead);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errorCount);
                        Interlocked.Increment(ref requestCount);
                        Logger.LogDebug(ex, "WAN {Direction} worker request failed", direction);
                        try { await Task.Delay(100, linked.Token); } catch { break; }
                    }
                }
            }, linked.Token);
        }

        // Launch latency probe task
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
                    using var response = await probeClient.GetAsync(probeUrl, linked.Token);
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

        stop.Cancel();
        try { await Task.WhenAll(tasks); }
        catch { /* Workers throw OperationCanceledException on cancellation */ }
        try { await probeTask; }
        catch { /* Probe throws on cancellation */ }

        // Log summary
        var totalRequests = Interlocked.Read(ref requestCount);
        var totalErrors = Interlocked.Read(ref errorCount);
        Logger.LogDebug(
            "WAN {Direction} phase complete: {Requests} requests, {Errors} errors, {Bytes} bytes, {Samples} throughput samples",
            direction, totalRequests, totalErrors, Interlocked.Read(ref totalBytes), mbpsSamples.Count);
        if (totalErrors > 0)
            Logger.LogDebug("WAN {Direction} had {Errors}/{Requests} failed requests ({Pct:F0}% error rate)",
                direction, totalErrors, totalRequests, totalErrors * 100.0 / Math.Max(totalRequests, 1));

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
            var count = sortedLatencies.Count;
            loadedLatencyMs = count % 2 == 0
                ? (sortedLatencies[count / 2 - 1] + sortedLatencies[count / 2]) / 2.0
                : sortedLatencies[count / 2];

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
}
