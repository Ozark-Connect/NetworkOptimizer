using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running WAN speed tests via UWN's distributed HTTP speed test network.
/// Executes the local uwnspeedtest Go binary and parses its JSON output.
/// </summary>
public class UwnSpeedTestService : WanSpeedTestServiceBase
{
    private readonly IConfiguration _configuration;
    private readonly UniFiConnectionService _connectionService;

    protected override SpeedTestDirection Direction => SpeedTestDirection.UwnWan;

    // Include historical Cloudflare WAN results so the UI shows all server-side WAN test history
    protected override SpeedTestDirection[] OwnedDirections =>
        [SpeedTestDirection.UwnWan, SpeedTestDirection.CloudflareWan];

    private int Streams => MaxMode ? 24 : 16;
    private int ServerCount => MaxMode ? 6 : 4;

    public UwnSpeedTestService(
        ILogger<UwnSpeedTestService> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        INetworkPathAnalyzer pathAnalyzer,
        IConfiguration configuration,
        Iperf3ServerService iperf3ServerService,
        UniFiConnectionService connectionService)
        : base(dbFactory, pathAnalyzer, logger, iperf3ServerService)
    {
        _configuration = configuration;
        _connectionService = connectionService;
    }

    protected override async Task<Iperf3Result?> RunTestCoreAsync(
        Action<string, int, string?> report,
        CancellationToken cancellationToken)
    {
        var binaryPath = GetLocalBinaryPath();
        if (!File.Exists(binaryPath))
            throw new InvalidOperationException(
                $"UWN speed test binary not found at {binaryPath}. " +
                "Ensure the binary is built for this platform.");

        Logger.LogInformation(
            "Starting UWN WAN speed test via local binary ({Streams} streams, {Servers} servers, binary: {Binary})",
            Streams, ServerCount, Path.GetFileName(binaryPath));

        report("Starting", 0, null);

        var args = $"-streams {Streams} -servers {ServerCount} -duration 10 -timeout 90";

        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start uwnspeedtest process");

        // Track metadata from stderr for early UI display
        string? serverInfo = null;
        string? wanIp = null;
        string? isp = null;

        // Parse stderr lines for progress reporting
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
                {
                    Logger.LogDebug("uwnspeedtest: {Line}", line);

                    if (line.StartsWith("Acquiring"))
                        report("Acquiring token", 2, "Getting test token...");
                    else if (line.StartsWith("IP: "))
                    {
                        // Parse "IP: 1.2.3.4 (ISP Name)"
                        var content = line[4..];
                        var parenIdx = content.IndexOf(" (", StringComparison.Ordinal);
                        if (parenIdx >= 0)
                        {
                            wanIp = content[..parenIdx].Trim();
                            isp = content[(parenIdx + 2)..].TrimEnd(')');
                        }
                        else
                        {
                            wanIp = content.Trim();
                        }
                    }
                    else if (line.StartsWith("Discovering"))
                        report("Discovering servers", 5, "Finding nearby servers...");
                    else if (line.StartsWith("Found"))
                        report("Selecting servers", 7, line);
                    else if (line.StartsWith("Servers: "))
                    {
                        serverInfo = line[9..].Trim();
                        SetMetadata(new WanTestMetadata(
                            ServerInfo: serverInfo,
                            Location: isp ?? "",
                            WanIp: wanIp));
                        report("Servers selected", 8, serverInfo);
                    }
                    else if (line.StartsWith("Measuring latency"))
                        report("Testing latency", 10, null);
                    else if (line.StartsWith("Latency: "))
                        report("Latency measured", 15, line);
                    else if (line.StartsWith("Testing download"))
                        report("Testing download", 20, null);
                    else if (line.StartsWith("Download: "))
                        report("Download complete", 55, "Down: " + line[10..].Trim());
                    else if (line.StartsWith("Testing upload"))
                        report("Testing upload", 60, null);
                    else if (line.StartsWith("Upload: "))
                        report("Upload complete", 95, null);
                }
            }
            catch (OperationCanceledException) { /* expected */ }
        }, CancellationToken.None);

        // Read all stdout (JSON output)
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        // Wait for process with timeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("UWN speed test timed out after 120 seconds");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        await stderrTask;
        var stdout = await stdoutTask;

        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                $"UWN speed test binary produced no output (exit code: {process.ExitCode})");

        // Parse JSON output
        report("Processing", 96, null);
        var json = JsonSerializer.Deserialize<WanSpeedTestResult>(stdout, JsonOptions);
        if (json == null)
            throw new InvalidOperationException("Failed to parse speed test JSON output");

        if (!json.Success)
            throw new InvalidOperationException($"Speed test failed: {json.Error}");

        // Build result
        var primaryServerHost = !string.IsNullOrEmpty(json.Metadata?.ServerHost)
            ? json.Metadata.ServerHost : "UWN Test";
        var deviceName = !string.IsNullOrEmpty(json.Metadata?.Colo)
            ? json.Metadata.Colo : serverInfo ?? "UWN";
        var downloadMbps = (json.Download?.Bps ?? 0) / 1_000_000.0;
        var uploadMbps = (json.Upload?.Bps ?? 0) / 1_000_000.0;

        // Update metadata with final values from JSON
        var finalWanIp = !string.IsNullOrEmpty(json.Metadata?.Ip) ? json.Metadata.Ip : wanIp;
        var finalIsp = !string.IsNullOrEmpty(json.Metadata?.Country) ? json.Metadata.Country : isp;
        var finalServerInfo = !string.IsNullOrEmpty(json.Metadata?.Colo) ? json.Metadata.Colo : serverInfo;
        SetMetadata(new WanTestMetadata(
            ServerInfo: finalServerInfo ?? "UWN",
            Location: finalIsp ?? "",
            WanIp: finalWanIp));

        var serverIp = _configuration["HOST_IP"];

        var result = new Iperf3Result
        {
            Direction = SpeedTestDirection.UwnWan,
            DeviceHost = primaryServerHost,
            DeviceName = deviceName,
            DeviceType = "WAN",
            LocalIp = serverIp,
            DownloadBitsPerSecond = json.Download?.Bps ?? 0,
            UploadBitsPerSecond = json.Upload?.Bps ?? 0,
            DownloadBytes = json.Download?.Bytes ?? 0,
            UploadBytes = json.Upload?.Bytes ?? 0,
            PingMs = json.Latency?.UnloadedMs ?? 0,
            JitterMs = json.Latency?.JitterMs ?? 0,
            DownloadLatencyMs = json.Download?.LoadedLatencyMs > 0 ? json.Download.LoadedLatencyMs : null,
            DownloadJitterMs = json.Download?.LoadedJitterMs > 0 ? json.Download.LoadedJitterMs : null,
            UploadLatencyMs = json.Upload?.LoadedLatencyMs > 0 ? json.Upload.LoadedLatencyMs : null,
            UploadJitterMs = json.Upload?.LoadedJitterMs > 0 ? json.Upload.LoadedJitterMs : null,
            TestTime = DateTime.UtcNow,
            Success = true,
            ParallelStreams = json.Streams,
            DurationSeconds = json.DurationSeconds,
        };

        // Identify WAN connection
        try
        {
            // In max mode with multi-WAN, traffic load-balances across WANs so mark as All WANs
            var isMultiWan = false;
            if (MaxMode && _connectionService.IsConnected)
            {
                var networks = await _connectionService.GetNetworksAsync();
                isMultiWan = networks.Count(n => n.IsWan && n.Enabled) > 1;
            }

            if (MaxMode && isMultiWan)
            {
                result.WanNetworkGroup = "ALL_WANS";
                result.WanName = "All WANs";
            }
            else
            {
                var (wanGroup, wanName) = await PathAnalyzer.IdentifyWanConnectionAsync(
                    finalWanIp ?? "", downloadMbps, uploadMbps, cancellationToken);
                result.WanNetworkGroup = wanGroup;
                result.WanName = wanName;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not identify WAN connection");
        }

        Logger.LogInformation(
            "UWN WAN speed test complete: Down {Download:F1} Mbps, Up {Upload:F1} Mbps, Latency {Latency:F1} ms",
            downloadMbps, uploadMbps, json.Latency?.UnloadedMs ?? 0);

        report("Complete", 100, $"Down: {downloadMbps:F1} / Up: {uploadMbps:F1} Mbps");

        return result;
    }

    protected override Iperf3Result CreateFailedResult(string errorMessage) => new()
    {
        Direction = SpeedTestDirection.UwnWan,
        DeviceHost = "UWN Test",
        DeviceName = "UWN",
        DeviceType = "WAN",
        TestTime = DateTime.UtcNow,
        Success = false,
        ErrorMessage = errorMessage,
    };

    #region Binary Resolution

    /// <summary>
    /// Resolves the local uwnspeedtest binary path based on the current platform.
    /// Binary naming convention: uwnspeedtest-{os}-{arch}[.exe]
    /// </summary>
    private static string GetLocalBinaryPath()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin"
            : "linux";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            _ => "amd64"
        };

        var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
        var binaryName = $"uwnspeedtest-{os}-{arch}{ext}";
        return Path.Combine(AppContext.BaseDirectory, "tools", binaryName);
    }

    #endregion

    #region JSON Models

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class WanSpeedTestResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public WanMetadata? Metadata { get; set; }
        public WanLatency? Latency { get; set; }
        public WanThroughput? Download { get; set; }
        public WanThroughput? Upload { get; set; }
        public int Streams { get; set; }
        public int DurationSeconds { get; set; }
    }

    private sealed class WanMetadata
    {
        public string Ip { get; set; } = "";
        public string Colo { get; set; } = "";
        public string Country { get; set; } = "";
        public string ServerHost { get; set; } = "";
    }

    private sealed class WanLatency
    {
        public double UnloadedMs { get; set; }
        public double JitterMs { get; set; }
    }

    private sealed class WanThroughput
    {
        public double Bps { get; set; }
        public long Bytes { get; set; }
        public double LoadedLatencyMs { get; set; }
        public double LoadedJitterMs { get; set; }
    }

    #endregion
}
