using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running WAN speed tests via UWN's distributed HTTP speed test network.
/// Uses multiple geographically close servers over plain HTTP to eliminate TLS overhead.
/// Workers are distributed round-robin across selected servers to aggregate bandwidth.
/// </summary>
public class UwnSpeedTestService : WanSpeedTestServiceBase
{
    private const string TokenUrl = "https://sp-dir.uwn.com/api/v1/tokens";
    private const string ServersUrl = "https://sp-dir.uwn.com/api/v2/servers";
    private const string IpInfoUrl = "https://sp-dir.uwn.com/api/v1/ip";

    private const string UserAgent = "ui-speed-linux-arm64/1.3.4";

    private const int Concurrency = 8;
    private const int ServerCount = 4;
    private static readonly TimeSpan DownloadDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UploadDuration = TimeSpan.FromSeconds(10);
    private const int UploadBytesPerRequest = 5_000_000; // 5 MB per upload request

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;

    protected override SpeedTestDirection Direction => SpeedTestDirection.UwnWan;

    // Include historical Cloudflare WAN results so the UI shows all server-side WAN test history
    protected override SpeedTestDirection[] OwnedDirections =>
        [SpeedTestDirection.UwnWan, SpeedTestDirection.CloudflareWan];

    public UwnSpeedTestService(
        ILogger<UwnSpeedTestService> logger,
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        INetworkPathAnalyzer pathAnalyzer,
        IConfiguration configuration,
        Iperf3ServerService iperf3ServerService)
        : base(dbFactory, pathAnalyzer, logger, iperf3ServerService)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    protected override async Task<Iperf3Result?> RunTestCoreAsync(
        Action<string, int, string?> report,
        CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting UWN WAN speed test ({Concurrency} concurrent connections, {Servers} servers)",
            Concurrency, ServerCount);

        report("Connecting", 0, null);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        // Phase 1: Acquire token and fetch IP info (0-3%)
        report("Acquiring token", 1, "Getting test token...");
        var token = await FetchTokenAsync(client, cancellationToken);
        var ipInfo = await FetchIpInfoAsync(client, cancellationToken);
        if (ipInfo != null)
            Logger.LogInformation("External IP: {Ip} ({Isp}), location: {Lat},{Lon}", ipInfo.Ip, ipInfo.Isp, ipInfo.Lat, ipInfo.Lon);

        // Phase 2: Discover servers (3-8%)
        report("Discovering servers", 3, "Finding nearby servers...");
        var candidates = await DiscoverServersAsync(client, cancellationToken);
        Logger.LogInformation("Discovered {Count} UWN servers, selecting best {Target}", candidates.Count, ServerCount);

        report("Selecting servers", 5, $"Pinging {Math.Min(candidates.Count, ServerCount * 2)} servers...");
        var servers = await SelectBestServersAsync(client, token, candidates, ServerCount, cancellationToken);
        var serverDesc = string.Join(", ", servers.Select(s => $"{s.City}/{s.Country} ({s.LatencyMs:F0}ms)"));
        // Extract hostname/IP from primary server URL for path analysis and PBR
        var primaryServerHost = new Uri(servers[0].Url).Host;
        Logger.LogInformation("Selected servers: {Servers} (primary: {PrimaryHost})", serverDesc, primaryServerHost);

        SetMetadata(new WanTestMetadata(
            ServerInfo: serverDesc,
            Location: ipInfo != null ? $"{ipInfo.Isp} ({ipInfo.Ip})" : servers[0].City + ", " + servers[0].Country,
            WanIp: ipInfo?.Ip));
        report("Servers selected", 8, serverDesc);

        // Phase 3: Latency (8-15%)
        report("Testing latency", 9, null);
        var (latencyMs, jitterMs) = await MeasureLatencyAsync(client, servers[0], token, cancellationToken);
        Logger.LogInformation("Latency: {Latency:F1} ms, Jitter: {Jitter:F1} ms", latencyMs, jitterMs);
        report("Testing latency", 15, $"Latency: {latencyMs:F1} ms / {jitterMs:F1} ms jitter");

        // Phase 4: Download (15-55%)
        report("Testing download", 16, null);
        var (downloadBps, downloadBytes, dlLatencyMs, dlJitterMs) = await MeasureThroughputAsync(
            isUpload: false,
            DownloadDuration,
            servers,
            token,
            pct => report("Testing download", 15 + (int)(pct * 40), null),
            cancellationToken);
        var downloadMbps = downloadBps / 1_000_000.0;
        Logger.LogInformation("Download: {Speed:F1} Mbps ({Bytes} bytes, {Workers} workers across {Servers} servers), loaded latency: {Latency:F1} ms",
            downloadMbps, downloadBytes, Concurrency, servers.Count, dlLatencyMs);
        report("Download complete", 55, $"Down: {downloadMbps:F1} Mbps");

        // Phase 5: Upload (55-95%)
        report("Testing upload", 56, null);
        var (uploadBps, uploadBytes, ulLatencyMs, ulJitterMs) = await MeasureThroughputAsync(
            isUpload: true,
            UploadDuration,
            servers,
            token,
            pct => report("Testing upload", 55 + (int)(pct * 40), null),
            cancellationToken);
        var uploadMbps = uploadBps / 1_000_000.0;
        Logger.LogInformation("Upload: {Speed:F1} Mbps ({Bytes} bytes, {Workers} workers across {Servers} servers), loaded latency: {Latency:F1} ms",
            uploadMbps, uploadBytes, Concurrency, servers.Count, ulLatencyMs);
        report("Upload complete", 95, null);

        // Phase 6: Build result (95-100%)
        report("Saving", 96, null);

        var serverIp = _configuration["HOST_IP"];

        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.UwnWan,
            DeviceHost = primaryServerHost,
            DeviceName = serverDesc,
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

        // Identify WAN connection using external IP from UWN directory
        try
        {
            var (wanGroup, wanName) = await PathAnalyzer.IdentifyWanConnectionAsync(
                ipInfo?.Ip ?? "", downloadMbps, uploadMbps, cancellationToken);
            result.WanNetworkGroup = wanGroup;
            result.WanName = wanName;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not identify WAN connection");
        }

        Logger.LogInformation(
            "UWN WAN speed test complete: Down {Download:F1} Mbps, Up {Upload:F1} Mbps, Latency {Latency:F1} ms",
            downloadMbps, uploadMbps, latencyMs);

        report("Complete", 100, $"Down: {downloadMbps:F1} / Up: {uploadMbps:F1} Mbps");

        return result;
    }

    protected override Iperf3Result CreateFailedResult(string errorMessage) => new()
    {
        Direction = SpeedTestDirection.UwnWan,
        DeviceHost = "sp-dir.uwn.com",
        DeviceName = "UWN",
        DeviceType = "WAN",
        TestTime = DateTime.UtcNow,
        Success = false,
        ErrorMessage = errorMessage,
    };

    #region UWN Protocol

    private record UwnIpInfo(string Ip, string? Isp, double Lat, double Lon);

    private static async Task<UwnIpInfo?> FetchIpInfoAsync(HttpClient client, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(IpInfoUrl, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new UwnIpInfo(
                Ip: root.GetProperty("ip").GetString() ?? "",
                Isp: root.TryGetProperty("isp", out var isp) ? isp.GetString() : null,
                Lat: root.TryGetProperty("lat", out var lat) ? lat.GetDouble() : 0,
                Lon: root.TryGetProperty("lon", out var lon) ? lon.GetDouble() : 0);
        }
        catch
        {
            return null;
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.TryAddWithoutValidation("x-test-token", token);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private static async Task<string> FetchTokenAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostAsync(TokenUrl, null, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("Empty token returned from UWN");
        return token;
    }

    private record UwnServer(string Url, string Provider, string City, string Country, double Lat, double Lon)
    {
        public double LatencyMs { get; set; }
    }

    private static async Task<List<UwnServer>> DiscoverServersAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(ServersUrl, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var servers = new List<UwnServer>();

        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            servers.Add(new UwnServer(
                Url: elem.GetProperty("url").GetString() ?? "",
                Provider: elem.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "",
                City: elem.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "",
                Country: elem.TryGetProperty("country", out var co) ? co.GetString() ?? "" : "",
                Lat: elem.TryGetProperty("lat", out var lat) ? lat.GetDouble() : 0,
                Lon: elem.TryGetProperty("lon", out var lon) ? lon.GetDouble() : 0));
        }

        return servers;
    }

    private async Task<List<UwnServer>> SelectBestServersAsync(
        HttpClient client, string token, List<UwnServer> candidates, int count, CancellationToken ct)
    {
        // Ping top candidates (up to 2x count), return best N by RTT
        var pingCount = Math.Min(count * 2, candidates.Count);
        var pinged = new List<UwnServer>();

        for (int i = 0; i < pingCount; i++)
        {
            var server = candidates[i];
            try
            {
                var rtt = await PingServerAsync(client, server.Url, token, ct);
                server.LatencyMs = rtt;
                pinged.Add(server);
            }
            catch
            {
                // Skip unreachable servers
            }
        }

        if (pinged.Count == 0)
            throw new InvalidOperationException("No UWN servers responded to ping");

        return pinged
            .OrderBy(s => s.LatencyMs)
            .Take(count)
            .ToList();
    }

    private static async Task<double> PingServerAsync(HttpClient client, string serverUrl, string token, CancellationToken ct)
    {
        var url = serverUrl + "/ping";
        double minRtt = double.MaxValue;

        for (int i = 0; i < 3; i++)
        {
            using var request = CreateRequest(HttpMethod.Get, url, token);

            var sw = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, ct);
            sw.Stop();

            if (response.IsSuccessStatusCode && sw.Elapsed.TotalMilliseconds < minRtt)
                minRtt = sw.Elapsed.TotalMilliseconds;
        }

        if (minRtt == double.MaxValue)
            throw new InvalidOperationException("All pings failed");

        return minRtt;
    }

    private static async Task<(double LatencyMs, double JitterMs)> MeasureLatencyAsync(
        HttpClient client, UwnServer server, string token, CancellationToken ct)
    {
        var url = server.Url + "/ping";
        var latencies = new List<double>();

        // Warmup
        try
        {
            using var warmReq = CreateRequest(HttpMethod.Get, url, token);
            using var resp = await client.SendAsync(warmReq, ct);
        }
        catch { /* warmup failure is ok */ }

        for (int i = 0; i < 20; i++)
        {
            using var request = CreateRequest(HttpMethod.Get, url, token);

            var sw = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, ct);
            sw.Stop();

            latencies.Add(sw.Elapsed.TotalMilliseconds);
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

    #endregion

    #region Throughput

    private async Task<(double BitsPerSecond, long TotalBytes, double LoadedLatencyMs, double LoadedJitterMs)> MeasureThroughputAsync(
        bool isUpload,
        TimeSpan duration,
        List<UwnServer> servers,
        string token,
        Action<double> onProgress,
        CancellationToken ct)
    {
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        long totalBytes = 0;
        long errorCount = 0;
        long requestCount = 0;

        var loadedLatencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var uploadPayload = isUpload ? new byte[UploadBytesPerRequest] : null;
        var direction = isUpload ? "upload" : "download";

        // Launch workers distributed round-robin across servers
        var tasks = new Task[Concurrency];
        for (int w = 0; w < Concurrency; w++)
        {
            var server = servers[w % servers.Count];
            tasks[w] = Task.Run(async () =>
            {
                using var workerClient = _httpClientFactory.CreateClient();
                workerClient.Timeout = TimeSpan.FromSeconds(60);
                var readBuffer = isUpload ? null : new byte[81920];

                while (!linked.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (isUpload)
                        {
                            var url = server.Url + "/upload";
                            using var content = new ProgressContent(uploadPayload!, bytesWritten =>
                                Interlocked.Add(ref totalBytes, bytesWritten));
                            using var request = CreateRequest(HttpMethod.Post, url, token, content);

                            using var response = await workerClient.SendAsync(request, linked.Token);
                            Interlocked.Increment(ref requestCount);
                            await response.Content.CopyToAsync(Stream.Null, linked.Token);
                            if (!response.IsSuccessStatusCode)
                            {
                                Interlocked.Increment(ref errorCount);
                                await Task.Delay(100, linked.Token);
                            }
                        }
                        else
                        {
                            var url = server.Url + "/download";
                            using var request = CreateRequest(HttpMethod.Get, url, token);

                            using var response = await workerClient.SendAsync(request,
                                HttpCompletionOption.ResponseHeadersRead, linked.Token);
                            Interlocked.Increment(ref requestCount);
                            if (!response.IsSuccessStatusCode)
                            {
                                Interlocked.Increment(ref errorCount);
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
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errorCount);
                        Interlocked.Increment(ref requestCount);
                        Logger.LogDebug(ex, "UWN {Direction} worker request failed", direction);
                        try { await Task.Delay(100, linked.Token); } catch { break; }
                    }
                }
            }, linked.Token);
        }

        // Launch latency probe against first server
        var probeTask = Task.Run(async () =>
        {
            using var probeClient = _httpClientFactory.CreateClient();
            probeClient.Timeout = TimeSpan.FromSeconds(10);
            var probeUrl = servers[0].Url + "/ping";

            while (!linked.Token.IsCancellationRequested)
            {
                try
                {
                    using var request = CreateRequest(HttpMethod.Get, probeUrl, token);

                    var sw = Stopwatch.StartNew();
                    using var response = await probeClient.SendAsync(request, linked.Token);
                    sw.Stop();

                    if (sw.Elapsed.TotalMilliseconds > 0)
                        loadedLatencies.Add(sw.Elapsed.TotalMilliseconds);

                    await Task.Delay(500, linked.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { /* Probe failed, skip */ }
            }
        }, linked.Token);

        // Measure aggregate throughput
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
        catch { /* Workers cancelled */ }
        try { await probeTask; }
        catch { /* Probe cancelled */ }

        // Log summary
        var totalRequests = Interlocked.Read(ref requestCount);
        var totalErrors = Interlocked.Read(ref errorCount);
        Logger.LogDebug(
            "UWN {Direction} phase complete: {Requests} requests, {Errors} errors, {Bytes} bytes, {Samples} throughput samples",
            direction, totalRequests, totalErrors, Interlocked.Read(ref totalBytes), mbpsSamples.Count);

        // Compute mean Mbps (skip warmup)
        var finalBytes = Interlocked.Read(ref totalBytes);
        if (mbpsSamples.Count == 0)
            return (0, finalBytes, 0, 0);

        var skipCount = (int)(mbpsSamples.Count * 0.20);
        var steadySamples = mbpsSamples.Skip(skipCount).ToList();
        if (steadySamples.Count == 0)
            steadySamples = mbpsSamples;

        var meanMbps = steadySamples.Average();
        var bitsPerSecond = meanMbps * 1_000_000.0;

        // Compute loaded latency
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

    #endregion
}
