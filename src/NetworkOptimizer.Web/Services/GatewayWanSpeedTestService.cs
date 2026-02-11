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
/// Deploys the cfspeedtest binary to the gateway and runs it on a specific WAN interface.
/// This measures true WAN throughput without LAN traversal overhead.
/// </summary>
public class GatewayWanSpeedTestService
{
    private const string RemoteBinaryPath = "/data/cfspeedtest";
    private const string LocalBinaryName = "cfspeedtest-linux-arm64";

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
    /// Check if the cfspeedtest binary is deployed and up to date.
    /// Compares remote file size against local binary to detect updates.
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

            // Check if the remote binary matches the local one by file size
            var localPath = Path.Combine(AppContext.BaseDirectory, "tools", LocalBinaryName);
            if (File.Exists(localPath))
            {
                var localSize = new FileInfo(localPath).Length;
                var sizeResult = await _gatewaySsh.RunCommandAsync(
                    $"stat -c %s {RemoteBinaryPath} 2>/dev/null || wc -c < {RemoteBinaryPath}",
                    TimeSpan.FromSeconds(10));

                if (sizeResult.success && long.TryParse(sizeResult.output.Trim(), out var remoteSize))
                {
                    if (localSize != remoteSize)
                    {
                        _logger.LogInformation("cfspeedtest binary size mismatch (local: {Local}, remote: {Remote}) - update needed",
                            localSize, remoteSize);
                        return (true, true);
                    }
                }
            }

            return (true, false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check cfspeedtest binary status on gateway");
            return (false, false);
        }
    }

    /// <summary>
    /// Deploy or update the cfspeedtest binary to the gateway via SFTP.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeployBinaryAsync(CancellationToken ct = default)
    {
        try
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "tools", LocalBinaryName);
            if (!File.Exists(localPath))
            {
                _logger.LogWarning("cfspeedtest binary not found at {Path}", localPath);
                return (false, "Gateway speed test binary not found. It may not be included in this build.");
            }

            var settings = await _gatewaySsh.GetSettingsAsync();
            if (string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
                return (false, "Gateway SSH not configured");

            // Get connection info for SFTP upload
            var connection = GetConnectionInfo(settings);

            _logger.LogInformation("Deploying cfspeedtest binary to gateway {Host}", settings.Host);
            await _sshClient.UploadBinaryAsync(connection, localPath, RemoteBinaryPath, ct);

            // Make executable
            var chmodResult = await _gatewaySsh.RunCommandAsync(
                $"chmod +x {RemoteBinaryPath}", TimeSpan.FromSeconds(10), ct);

            if (!chmodResult.success)
            {
                _logger.LogWarning("Failed to chmod cfspeedtest: {Output}", chmodResult.output);
                return (false, $"Failed to set binary permissions: {chmodResult.output}");
            }

            // Verify
            var versionResult = await _gatewaySsh.RunCommandAsync(
                $"{RemoteBinaryPath} -version", TimeSpan.FromSeconds(10), ct);

            if (versionResult.success)
            {
                _logger.LogInformation("cfspeedtest binary deployed successfully: {Version}", versionResult.output.Trim());
                return (true, null);
            }

            return (false, $"Binary deployed but version check failed: {versionResult.output}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy cfspeedtest binary to gateway");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Run a gateway-direct WAN speed test on a specific interface.
    /// </summary>
    public async Task<Iperf3Result?> RunTestAsync(
        string interfaceName,
        string? wanNetworkGroup,
        string? wanName,
        Action<(string Phase, int Percent, string? Status)>? onProgress = null,
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
            _logger.LogInformation("Starting gateway WAN speed test on interface {Interface}", interfaceName);

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

            // Phase 2: Run test via SSH (10-95%)
            // Simulate progress based on known timing (~30s total: 3s latency, 12s download, 12s upload, 3s finalize)
            Report("Testing", 12, "Measuring latency...");

            var command = $"{RemoteBinaryPath} --interface {interfaceName} 2>/dev/null";
            var sshTask = _gatewaySsh.RunCommandAsync(
                command, TimeSpan.FromSeconds(120), cancellationToken);

            var progressSteps = new (int Percent, string Status, int DelayMs)[]
            {
                (15, "Measuring latency...", 2500),
                (22, "Testing download...", 1800),
                (32, "Testing download...", 1800),
                (42, "Testing download...", 1800),
                (52, "Testing download...", 1800),
                (58, "Testing download...", 1800),
                (65, "Testing upload...", 1800),
                (72, "Testing upload...", 1800),
                (78, "Testing upload...", 1800),
                (84, "Testing upload...", 1800),
                (90, "Testing upload...", 1800),
            };

            foreach (var step in progressSteps)
            {
                if (sshTask.IsCompleted) break;
                try { await Task.WhenAny(sshTask, Task.Delay(step.DelayMs, cancellationToken)); }
                catch (OperationCanceledException) { break; }
                if (!sshTask.IsCompleted)
                    Report("Testing", step.Percent, step.Status);
            }

            var result = await sshTask;

            if (!result.success)
            {
                var error = $"Gateway speed test failed: {result.output}";
                _logger.LogWarning(error);
                Report("Error", 0, error);
                return SaveFailedResult(error, wanNetworkGroup, wanName);
            }

            // Phase 3: Parse JSON output (95-98%)
            Report("Parsing", 95, "Processing results...");
            var testResult = ParseResult(result.output, interfaceName, wanNetworkGroup, wanName);

            if (testResult == null)
            {
                var error = "Failed to parse speed test output";
                Report("Error", 0, error);
                return SaveFailedResult(error, wanNetworkGroup, wanName);
            }

            // Phase 4: Save to DB (98-100%)
            Report("Saving", 98, "Saving results...");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            db.Iperf3Results.Add(testResult);
            await db.SaveChangesAsync(cancellationToken);
            var resultId = testResult.Id;

            _logger.LogInformation(
                "Gateway WAN speed test complete ({Interface}): Down {Download:F1} Mbps, Up {Upload:F1} Mbps, Latency {Latency:F1} ms",
                interfaceName, testResult.DownloadMbps, testResult.UploadMbps, testResult.PingMs);

            Report("Complete", 100, $"Down: {testResult.DownloadMbps:F1} / Up: {testResult.UploadMbps:F1} Mbps");
            lock (_lock) _lastCompletedResult = testResult;

            // Background path analysis
            var resolvedWanGroup = testResult.WanNetworkGroup;
            _ = Task.Run(async () => await AnalyzePathInBackgroundAsync(resultId, resolvedWanGroup: resolvedWanGroup), CancellationToken.None);

            return testResult;
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

    /// <summary>
    /// Get recent gateway WAN speed test results.
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsAsync(int count = 50, int hours = 0)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Iperf3Results
            .Where(r => r.Direction == SpeedTestDirection.CloudflareWanGateway);

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
        if (result == null || result.Direction != SpeedTestDirection.CloudflareWanGateway)
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
        if (result == null || result.Direction != SpeedTestDirection.CloudflareWanGateway)
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
        if (result == null || result.Direction != SpeedTestDirection.CloudflareWanGateway)
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
            var json = JsonSerializer.Deserialize<CfSpeedTestResult>(jsonOutput, JsonOptions);
            if (json == null) return null;

            if (!json.Success)
            {
                _logger.LogWarning("Gateway speed test reported failure: {Error}", json.Error);
                return new Iperf3Result
                {
                    Direction = SpeedTestDirection.CloudflareWanGateway,
                    DeviceHost = "speed.cloudflare.com",
                    DeviceName = $"Gateway ({interfaceName})",
                    DeviceType = "WAN",
                    WanNetworkGroup = wanNetworkGroup,
                    WanName = wanName,
                    TestTime = DateTime.UtcNow,
                    Success = false,
                    ErrorMessage = json.Error ?? "Test failed"
                };
            }

            var colo = json.Metadata?.Colo ?? "";
            var country = json.Metadata?.Country ?? "";
            var edgeInfo = !string.IsNullOrEmpty(colo)
                ? $"{colo} - {CloudflareSpeedTestService.GetCityName(colo)}, {country}"
                : "Cloudflare";

            return new Iperf3Result
            {
                Direction = SpeedTestDirection.CloudflareWanGateway,
                DeviceHost = "speed.cloudflare.com",
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
            _logger.LogError(ex, "Failed to parse cfspeedtest JSON output");
            return null;
        }
    }

    private Iperf3Result? SaveFailedResult(string? errorMessage, string? wanNetworkGroup, string? wanName)
    {
        try
        {
            var failedResult = new Iperf3Result
            {
                Direction = SpeedTestDirection.CloudflareWanGateway,
                DeviceHost = "speed.cloudflare.com",
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

            var path = await _pathAnalyzer.CalculatePathAsync(
                result.DeviceHost, result.LocalIp, retryOnFailure: true,
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
    private sealed class CfSpeedTestResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public CfMetadata? Metadata { get; set; }
        public CfLatency? Latency { get; set; }
        public CfThroughput? Download { get; set; }
        public CfThroughput? Upload { get; set; }
        public int Streams { get; set; }
        public int DurationSeconds { get; set; }
    }

    private sealed class CfMetadata
    {
        public string Ip { get; set; } = "";
        public string Colo { get; set; } = "";
        public string Country { get; set; } = "";
    }

    private sealed class CfLatency
    {
        public double UnloadedMs { get; set; }
        public double JitterMs { get; set; }
    }

    private sealed class CfThroughput
    {
        public double Bps { get; set; }
        public long Bytes { get; set; }
        public double LoadedLatencyMs { get; set; }
        public double LoadedJitterMs { get; set; }
    }
}
