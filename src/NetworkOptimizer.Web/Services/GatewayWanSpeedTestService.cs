using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running WAN speed tests directly on the gateway via SSH.
/// Deploys the uwnspeedtest binary to the gateway and runs it on a specific WAN interface,
/// using UWN's distributed HTTP speed test network for accurate measurement.
/// This measures true WAN throughput without LAN traversal overhead.
/// </summary>
public class GatewayWanSpeedTestService
{
    private const string RemoteBinaryPath = "/data/uwnspeedtest";
    private const string LocalBinaryName = "uwnspeedtest-linux-arm64";

    private readonly ILogger<GatewayWanSpeedTestService> _logger;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly SshClientService _sshClient;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly IServiceProvider _serviceProvider;

    // Observable test state (polled by UI components)
    private readonly object _lock = new();
    private bool _isRunning;
    private string _currentPhase = "";
    private int _currentPercent;
    private string? _currentStatus;
    private Iperf3Result? _lastCompletedResult;

    /// <summary>Whether a gateway WAN speed test is currently running.</summary>
    public bool IsRunning { get { lock (_lock) return _isRunning; } }

    /// <summary>Current test progress snapshot for UI polling.</summary>
    public (string Phase, int Percent, string? Status) CurrentProgress
    {
        get { lock (_lock) return (_currentPhase, _currentPercent, _currentStatus); }
    }

    /// <summary>Last completed result from the current session.</summary>
    public Iperf3Result? LastCompletedResult
    {
        get { lock (_lock) return _lastCompletedResult; }
    }

    /// <summary>Fired when background path analysis completes for a result.</summary>
    public event Action<int>? OnPathAnalysisComplete;

    public GatewayWanSpeedTestService(
        ILogger<GatewayWanSpeedTestService> logger,
        IGatewaySshService gatewaySsh,
        SshClientService sshClient,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        INetworkPathAnalyzer pathAnalyzer,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _gatewaySsh = gatewaySsh;
        _sshClient = sshClient;
        _dbFactory = dbFactory;
        _pathAnalyzer = pathAnalyzer;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Check if the uwnspeedtest binary is deployed and up to date.
    /// Compares MD5 hash of remote binary against local to detect updates.
    /// </summary>
    public async Task<(bool Deployed, bool NeedsUpdate)> CheckBinaryStatusAsync()
    {
        try
        {
            var settings = await _gatewaySsh.GetSettingsAsync();
            if (string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials || !settings.Enabled)
                return (false, false);

            var result = await _gatewaySsh.RunCommandAsync(
                $"{RemoteBinaryPath} -version", TimeSpan.FromSeconds(10));

            if (!result.success)
                return (false, false);

            // Compare MD5 hashes to detect updates (size comparison is unreliable)
            var localPath = Path.Combine(AppContext.BaseDirectory, "tools", LocalBinaryName);
            if (File.Exists(localPath))
            {
                var localHash = ComputeMd5(localPath);
                var hashResult = await _gatewaySsh.RunCommandAsync(
                    $"md5sum {RemoteBinaryPath} 2>/dev/null | cut -d' ' -f1",
                    TimeSpan.FromSeconds(10));

                if (hashResult.success)
                {
                    var remoteHash = hashResult.output.Trim();
                    if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation("uwnspeedtest binary hash mismatch (local: {Local}, remote: {Remote}) - update needed",
                            localHash, remoteHash);
                        return (true, true);
                    }
                }
            }

            return (true, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check uwnspeedtest binary status on gateway");
            return (false, false);
        }
    }

    private static string ComputeMd5(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = System.Security.Cryptography.MD5.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Deploy or update the uwnspeedtest binary to the gateway via SFTP.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeployBinaryAsync(CancellationToken ct = default)
    {
        try
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "tools", LocalBinaryName);
            if (!File.Exists(localPath))
            {
                _logger.LogWarning("uwnspeedtest binary not found at {Path}", localPath);
                return (false, "Gateway speed test binary not found. It may not be included in this build.");
            }

            var settings = await _gatewaySsh.GetSettingsAsync();
            if (string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
                return (false, "Gateway SSH not configured");

            // Get connection info for SFTP upload
            var connection = GetConnectionInfo(settings);

            _logger.LogInformation("Deploying uwnspeedtest binary to gateway {Host}", settings.Host);
            await _sshClient.UploadBinaryAsync(connection, localPath, RemoteBinaryPath, ct);

            // Make executable
            var chmodResult = await _gatewaySsh.RunCommandAsync(
                $"chmod +x {RemoteBinaryPath}", TimeSpan.FromSeconds(10), ct);

            if (!chmodResult.success)
            {
                _logger.LogWarning("Failed to chmod uwnspeedtest: {Output}", chmodResult.output);
                return (false, $"Failed to set binary permissions: {chmodResult.output}");
            }

            // Verify
            var versionResult = await _gatewaySsh.RunCommandAsync(
                $"{RemoteBinaryPath} -version", TimeSpan.FromSeconds(10), ct);

            if (versionResult.success)
            {
                _logger.LogInformation("uwnspeedtest binary deployed successfully: {Version}", versionResult.output.Trim());
                return (true, null);
            }

            return (false, $"Binary deployed but version check failed: {versionResult.output}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy uwnspeedtest binary to gateway");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Run a gateway-direct WAN speed test on a specific interface,
    /// or run parallel tests on all WAN interfaces simultaneously when allInterfaces is provided.
    /// </summary>
    public async Task<Iperf3Result?> RunTestAsync(
        string interfaceName,
        string? wanNetworkGroup,
        string? wanName,
        Action<(string Phase, int Percent, string? Status)>? onProgress = null,
        IReadOnlyList<WanInterfaceInfo>? allInterfaces = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Gateway WAN speed test already in progress");
                return null;
            }
            _isRunning = true;
            _lastCompletedResult = null;
        }

        try
        {
            var isParallel = allInterfaces != null && allInterfaces.Count > 1;
            _logger.LogInformation("Starting gateway WAN speed test on {Interface}",
                isParallel ? $"{allInterfaces!.Count} WAN links in parallel"
                : string.IsNullOrEmpty(interfaceName) ? "default route" : $"interface {interfaceName}");

            void Report(string phase, int percent, string? status)
            {
                lock (_lock) { _currentPhase = phase; _currentPercent = percent; _currentStatus = status; }
                onProgress?.Invoke((phase, percent, status));
            }

            // Phase 1: Check/deploy binary (0-10%)
            Report("Preparing", 2, "Checking gateway binary...");
            var (deployed, needsUpdate) = await CheckBinaryStatusAsync();
            if (!deployed || needsUpdate)
            {
                var action = needsUpdate ? "Updating" : "Deploying";
                Report("Deploying", 5, $"{action} speed test binary on gateway...");
                var (deploySuccess, deployError) = await DeployBinaryAsync(cancellationToken);
                if (!deploySuccess)
                {
                    Report("Error", 0, deployError);
                    return SaveFailedResult(deployError, wanNetworkGroup, wanName);
                }
            }
            Report("Preparing", 10, "Binary ready");

            // Phase 2: Run test(s) via SSH (10-95%)
            if (isParallel)
            {
                return await RunParallelWanTests(allInterfaces!, Report, cancellationToken);
            }

            return await RunSingleWanTest(interfaceName, wanNetworkGroup, wanName, Report, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Gateway WAN speed test cancelled");
            lock (_lock) { _currentPhase = "Cancelled"; _currentPercent = 0; _currentStatus = "Test cancelled"; }
            onProgress?.Invoke(("Cancelled", 0, "Test cancelled"));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gateway WAN speed test failed");
            lock (_lock) { _currentPhase = "Error"; _currentPercent = 0; _currentStatus = ex.Message; }
            onProgress?.Invoke(("Error", 0, ex.Message));
            return SaveFailedResult(ex.Message, wanNetworkGroup, wanName);
        }
        finally
        {
            lock (_lock) _isRunning = false;
        }
    }

    private async Task<Iperf3Result?> RunSingleWanTest(
        string interfaceName,
        string? wanNetworkGroup,
        string? wanName,
        Action<string, int, string?> report,
        CancellationToken cancellationToken)
    {
        report("Testing latency", 12, "Measuring latency...");

        var ifaceArg = "";
        if (!string.IsNullOrEmpty(interfaceName))
        {
            ValidateInterfaceName(interfaceName);
            ifaceArg = $" --interface {interfaceName}";
        }

        var command = $"{RemoteBinaryPath}{ifaceArg} -streams 16 -servers 4 2>/dev/null";
        var sshTask = _gatewaySsh.RunCommandAsync(
            command, TimeSpan.FromSeconds(120), cancellationToken);

        await AnimateProgress(sshTask, report, cancellationToken);

        var result = await sshTask;

        if (!result.success)
        {
            var error = $"Gateway speed test failed: {result.output}";
            _logger.LogWarning(error);
            report("Error", 0, error);
            return SaveFailedResult(error, wanNetworkGroup, wanName);
        }

        report("Parsing", 95, "Processing results...");
        var testResult = ParseResult(result.output, interfaceName, wanNetworkGroup, wanName);

        if (testResult == null)
        {
            var error = "Failed to parse speed test output";
            report("Error", 0, error);
            return SaveFailedResult(error, wanNetworkGroup, wanName);
        }

        return await SaveAndCompleteResult(testResult, interfaceName, report, cancellationToken);
    }

    private async Task<Iperf3Result?> RunParallelWanTests(
        IReadOnlyList<WanInterfaceInfo> interfaces,
        Action<string, int, string?> report,
        CancellationToken cancellationToken)
    {
        report("Testing", 12, $"Testing {interfaces.Count} WAN links in parallel...");

        // Validate all interface names up front
        foreach (var wan in interfaces)
            ValidateInterfaceName(wan.Interface);

        // Launch parallel SSH commands, one per WAN interface
        // Synchronized start: all binaries do setup independently, then begin throughput at the same time
        // Split connections proportionally: 4/16 total → divided by WAN count
        var startAt = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();
        var (perWanServers, perWanStreams) = interfaces.Count switch
        {
            2 => (2, 8),
            3 => (2, 4),
            4 => (1, 4),
            _ => (1, 2) // 5+ WANs
        };
        _logger.LogDebug("Parallel WAN test: startAt={StartAt} ({WANCount} WANs, {Servers} servers/{Streams} streams each)",
            startAt, interfaces.Count, perWanServers, perWanStreams);

        var sshTasks = interfaces.Select(wan =>
        {
            var cmd = $"{RemoteBinaryPath} --interface {wan.Interface} -servers {perWanServers} -streams {perWanStreams} -start-at {startAt} 2>/dev/null";
            _logger.LogDebug("WAN {Interface}: {Command}", wan.Interface, cmd);
            return _gatewaySsh.RunCommandAsync(cmd, TimeSpan.FromSeconds(120), cancellationToken);
        }).ToList();

        var allTask = Task.WhenAll(sshTasks);
        await AnimateProgress(allTask, report, cancellationToken);

        var results = await allTask;

        // Parse each result
        var parsedResults = new List<(WanSpeedTestResult json, WanInterfaceInfo wan)>();
        for (var i = 0; i < results.Length; i++)
        {
            var wan = interfaces[i];
            if (!results[i].success)
            {
                _logger.LogWarning("WAN test on {Interface} failed: {Output}", wan.Interface, results[i].output);
                continue;
            }

            try
            {
                var json = JsonSerializer.Deserialize<WanSpeedTestResult>(results[i].output, JsonOptions);
                if (json?.Success == true)
                    parsedResults.Add((json, wan));
                else
                    _logger.LogWarning("WAN test on {Interface} reported failure: {Error}", wan.Interface, json?.Error);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse result for {Interface}", wan.Interface);
            }
        }

        if (parsedResults.Count == 0)
        {
            report("Error", 0, "All WAN tests failed");
            return SaveFailedResult("All WAN interface tests failed", "ALL_WAN", "All WAN Links");
        }

        report("Processing", 92, $"{parsedResults.Count}/{interfaces.Count} WANs completed");

        var testResult = AggregateParallelResults(parsedResults);

        _logger.LogInformation(
            "Gateway WAN speed test complete ({Count} WANs): Down {Download:F1} Mbps, Up {Upload:F1} Mbps",
            parsedResults.Count, testResult.DownloadMbps, testResult.UploadMbps);

        return await SaveAndCompleteResult(testResult, "all WANs", report, cancellationToken);
    }

    private Iperf3Result AggregateParallelResults(List<(WanSpeedTestResult json, WanInterfaceInfo wan)> results)
    {
        double totalDownBps = 0, totalUpBps = 0;
        long totalDownBytes = 0, totalUpBytes = 0;
        var bestLatency = double.MaxValue;
        double worstJitter = 0;
        int totalStreams = 0, maxDuration = 0;
        var dlLatencies = new List<double>();
        var dlJitters = new List<double>();
        var ulLatencies = new List<double>();
        var ulJitters = new List<double>();
        var serverInfoParts = new List<string>();
        string? primaryServerHost = null;

        foreach (var (json, wan) in results)
        {
            totalDownBps += json.Download?.Bps ?? 0;
            totalUpBps += json.Upload?.Bps ?? 0;
            totalDownBytes += json.Download?.Bytes ?? 0;
            totalUpBytes += json.Upload?.Bytes ?? 0;
            totalStreams += json.Streams;
            if (json.DurationSeconds > maxDuration)
                maxDuration = json.DurationSeconds;

            if (json.Latency != null)
            {
                if (json.Latency.UnloadedMs < bestLatency) bestLatency = json.Latency.UnloadedMs;
                if (json.Latency.JitterMs > worstJitter) worstJitter = json.Latency.JitterMs;
            }

            if (json.Download?.LoadedLatencyMs > 0) dlLatencies.Add(json.Download.LoadedLatencyMs);
            if (json.Download?.LoadedJitterMs > 0) dlJitters.Add(json.Download.LoadedJitterMs);
            if (json.Upload?.LoadedLatencyMs > 0) ulLatencies.Add(json.Upload.LoadedLatencyMs);
            if (json.Upload?.LoadedJitterMs > 0) ulJitters.Add(json.Upload.LoadedJitterMs);

            var wanLabel = !string.IsNullOrEmpty(wan.Name) ? wan.Name : wan.Interface;
            var downMbps = (json.Download?.Bps ?? 0) / 1_000_000.0;
            var upMbps = (json.Upload?.Bps ?? 0) / 1_000_000.0;
            serverInfoParts.Add($"{wanLabel}: {downMbps:F0}/{upMbps:F0}");

            primaryServerHost ??= json.Metadata?.ServerHost;
        }

        return new Iperf3Result
        {
            Direction = SpeedTestDirection.UwnWanGateway,
            DeviceHost = primaryServerHost ?? "UWN Test",
            DeviceName = string.Join(" | ", serverInfoParts),
            DeviceType = "WAN",
            DownloadBitsPerSecond = totalDownBps,
            UploadBitsPerSecond = totalUpBps,
            DownloadBytes = totalDownBytes,
            UploadBytes = totalUpBytes,
            PingMs = bestLatency == double.MaxValue ? 0 : bestLatency,
            JitterMs = worstJitter,
            DownloadLatencyMs = dlLatencies.Count > 0 ? dlLatencies.Average() : null,
            DownloadJitterMs = dlJitters.Count > 0 ? dlJitters.Average() : null,
            UploadLatencyMs = ulLatencies.Count > 0 ? ulLatencies.Average() : null,
            UploadJitterMs = ulJitters.Count > 0 ? ulJitters.Average() : null,
            WanNetworkGroup = "ALL_WAN",
            WanName = "All WAN Links",
            ParallelStreams = totalStreams,
            DurationSeconds = maxDuration,
            TestTime = DateTime.UtcNow,
            Success = true,
        };
    }

    private async Task<Iperf3Result?> SaveAndCompleteResult(
        Iperf3Result testResult,
        string interfaceLabel,
        Action<string, int, string?> report,
        CancellationToken cancellationToken)
    {
        report("Saving", 98, "Saving results...");
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.Iperf3Results.Add(testResult);
        await db.SaveChangesAsync(cancellationToken);
        var resultId = testResult.Id;

        _logger.LogInformation(
            "Gateway WAN speed test complete ({Interface}): Down {Download:F1} Mbps, Up {Upload:F1} Mbps, Latency {Latency:F1} ms",
            interfaceLabel, testResult.DownloadMbps, testResult.UploadMbps, testResult.PingMs);

        report("Complete", 100, $"Down: {testResult.DownloadMbps:F1} / Up: {testResult.UploadMbps:F1} Mbps");
        lock (_lock) _lastCompletedResult = testResult;

        var resolvedWanGroup = testResult.WanNetworkGroup;
        _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(resultId, resolvedWanGroup), CancellationToken.None);

        return testResult;
    }

    private static async Task AnimateProgress(Task sshTask, Action<string, int, string?> report, CancellationToken ct)
    {
        var progressSteps = new (string Phase, int Percent, string Status, int DelayMs)[]
        {
            ("Testing latency", 15, "Measuring latency...", 2500),
            ("Testing download", 22, "Testing download...", 1800),
            ("Testing download", 32, "Testing download...", 1800),
            ("Testing download", 42, "Testing download...", 1800),
            ("Testing download", 52, "Testing download...", 1800),
            ("Testing download", 58, "Testing download...", 1800),
            ("Testing upload", 65, "Testing upload...", 1800),
            ("Testing upload", 72, "Testing upload...", 1800),
            ("Testing upload", 78, "Testing upload...", 1800),
            ("Testing upload", 84, "Testing upload...", 1800),
            ("Testing upload", 90, "Testing upload...", 1800),
        };

        foreach (var step in progressSteps)
        {
            if (sshTask.IsCompleted) break;
            try { await Task.WhenAny(sshTask, Task.Delay(step.DelayMs, ct)); }
            catch (OperationCanceledException) { break; }
            if (!sshTask.IsCompleted)
                report(step.Phase, step.Percent, step.Status);
        }
    }

    private static void ValidateInterfaceName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9._-]+$"))
            throw new ArgumentException($"Invalid interface name: {name}");
    }

    /// <summary>
    /// Get recent gateway WAN speed test results.
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Include historical Cloudflare gateway results alongside new UWN results
        var query = db.Iperf3Results
            .Where(r => r.Direction == SpeedTestDirection.UwnWanGateway
                      || r.Direction == SpeedTestDirection.CloudflareWanGateway);

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
    /// Delete a gateway WAN speed test result.
    /// </summary>
    public async Task<bool> DeleteResultAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || (result.Direction != SpeedTestDirection.UwnWanGateway
                            && result.Direction != SpeedTestDirection.CloudflareWanGateway))
            return false;

        db.Iperf3Results.Remove(result);
        await db.SaveChangesAsync();
        _logger.LogInformation("Deleted gateway WAN speed test result {Id}", id);
        return true;
    }

    /// <summary>
    /// Update notes for a gateway WAN speed test result.
    /// </summary>
    public async Task<bool> UpdateNotesAsync(int id, string? notes)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || (result.Direction != SpeedTestDirection.UwnWanGateway
                            && result.Direction != SpeedTestDirection.CloudflareWanGateway))
            return false;

        result.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Reassign WAN interface for a result and re-run path analysis.
    /// </summary>
    public async Task<bool> UpdateWanAssignmentAsync(int id, string wanNetworkGroup, string? wanName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var result = await db.Iperf3Results.FindAsync(id);
        if (result == null || (result.Direction != SpeedTestDirection.UwnWanGateway
                            && result.Direction != SpeedTestDirection.CloudflareWanGateway))
            return false;

        result.WanNetworkGroup = wanNetworkGroup;
        result.WanName = wanName;
        result.PathAnalysisJson = null;
        await db.SaveChangesAsync();

        _logger.LogInformation("Reassigned WAN for gateway result {Id} to {Group} ({Name})", id, wanNetworkGroup, wanName);
        _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(id, resolvedWanGroup: wanNetworkGroup), CancellationToken.None);

        return true;
    }

    private Iperf3Result? ParseResult(string jsonOutput, string interfaceName, string? wanNetworkGroup, string? wanName)
    {
        try
        {
            var json = JsonSerializer.Deserialize<WanSpeedTestResult>(jsonOutput, JsonOptions);
            if (json == null) return null;

            if (!json.Success)
            {
                _logger.LogWarning("Gateway speed test reported failure: {Error}", json.Error);
                return new Iperf3Result
                {
                    Direction = SpeedTestDirection.UwnWanGateway,
                    DeviceHost = "UWN Test",
                    DeviceName = $"Gateway ({interfaceName})",
                    DeviceType = "WAN",
                    WanNetworkGroup = wanNetworkGroup,
                    WanName = wanName,
                    TestTime = DateTime.UtcNow,
                    Success = false,
                    ErrorMessage = json.Error ?? "Test failed"
                };
            }

            var serverInfo = json.Metadata?.Colo ?? "";
            var edgeInfo = !string.IsNullOrEmpty(serverInfo) ? serverInfo : "UWN";
            var serverHost = !string.IsNullOrEmpty(json.Metadata?.ServerHost) ? json.Metadata.ServerHost : "UWN Test";

            return new Iperf3Result
            {
                Direction = SpeedTestDirection.UwnWanGateway,
                DeviceHost = serverHost,
                DeviceName = edgeInfo,
                DeviceType = "WAN",
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
                WanNetworkGroup = wanNetworkGroup,
                WanName = wanName,
                ParallelStreams = json.Streams,
                DurationSeconds = json.DurationSeconds,
                TestTime = DateTime.UtcNow,
                Success = true,
            };
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse uwnspeedtest JSON output");
            return null;
        }
    }

    private Iperf3Result? SaveFailedResult(string? errorMessage, string? wanNetworkGroup, string? wanName)
    {
        try
        {
            var failedResult = new Iperf3Result
            {
                Direction = SpeedTestDirection.UwnWanGateway,
                DeviceHost = "UWN Test",
                DeviceName = "Gateway",
                DeviceType = "WAN",
                WanNetworkGroup = wanNetworkGroup,
                WanName = wanName,
                TestTime = DateTime.UtcNow,
                Success = false,
                ErrorMessage = errorMessage,
            };
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();
            using var context = db.CreateDbContext();
            context.Iperf3Results.Add(failedResult);
            context.SaveChanges();
            return failedResult;
        }
        catch (Exception saveEx)
        {
            _logger.LogWarning(saveEx, "Failed to save error result");
            return null;
        }
    }

    private async Task AnalyzePathInBackgroundAsync(int resultId, string? resolvedWanGroup = null)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            await using var db = await _dbFactory.CreateDbContextAsync();
            var result = await db.Iperf3Results.FindAsync(resultId);
            if (result == null) return;

            // Gateway direct path: Cloudflare → WAN → Gateway (no LAN hops)
            var path = await _pathAnalyzer.CalculateGatewayDirectPathAsync(
                resolvedWanGroup: resolvedWanGroup);

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

            _logger.LogDebug("Gateway WAN speed test path analysis complete for result {Id}", resultId);
            OnPathAnalysisComplete?.Invoke(resultId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze path for gateway WAN speed test result {Id}", resultId);
        }
    }

    private SshConnectionInfo GetConnectionInfo(GatewaySshSettings settings)
    {
        // Use the credential protection service to decrypt the password
        using var scope = _serviceProvider.CreateScope();
        var credProtection = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.ICredentialProtectionService>();

        string? decryptedPassword = null;
        if (!string.IsNullOrEmpty(settings.Password))
            decryptedPassword = credProtection.Decrypt(settings.Password);

        return SshConnectionInfo.FromGatewaySettings(settings, decryptedPassword);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() }
    };

    // JSON deserialization models matching the Go binary output
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
}
