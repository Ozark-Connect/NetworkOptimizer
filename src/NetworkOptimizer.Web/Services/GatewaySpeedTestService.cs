using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running iperf3 speed tests to the gateway.
/// SSH operations are delegated to IGatewaySshService.
/// </summary>
public class GatewaySpeedTestService : IGatewaySpeedTestService
{
    private readonly ILogger<GatewaySpeedTestService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly SystemSettingsService _systemSettings;
    private readonly UniFiConnectionService _connectionService;
    private readonly IMemoryCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    // Track running tests per site
    private readonly ConcurrentDictionary<int, bool> _isTestRunning = new();
    private readonly ConcurrentDictionary<int, GatewaySpeedTestResult> _lastResult = new();

    // Site-specific path analyzers
    private readonly ConcurrentDictionary<int, INetworkPathAnalyzer> _pathAnalyzers = new();

    public GatewaySpeedTestService(
        ILogger<GatewaySpeedTestService> logger,
        IServiceProvider serviceProvider,
        IGatewaySshService gatewaySsh,
        SystemSettingsService systemSettings,
        UniFiConnectionService connectionService,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _gatewaySsh = gatewaySsh;
        _systemSettings = systemSettings;
        _connectionService = connectionService;
        _cache = cache;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Gets or creates a site-specific path analyzer.
    /// </summary>
    private INetworkPathAnalyzer GetPathAnalyzer(int siteId)
    {
        return _pathAnalyzers.GetOrAdd(siteId, id =>
        {
            var clientProvider = new SiteSpecificClientProvider(_connectionService, id);
            return new NetworkPathAnalyzer(clientProvider, _cache, _loggerFactory);
        });
    }

    #region Settings Management (delegated to IGatewaySshService)

    /// <summary>
    /// Get the gateway SSH settings for a site (creates default if none exist)
    /// </summary>
    public Task<GatewaySshSettings> GetSettingsAsync(int siteId, bool forceRefresh = false)
        => _gatewaySsh.GetSettingsAsync(siteId, forceRefresh);

    /// <summary>
    /// Save gateway SSH settings for a site
    /// </summary>
    public Task<GatewaySshSettings> SaveSettingsAsync(int siteId, GatewaySshSettings settings)
        => _gatewaySsh.SaveSettingsAsync(siteId, settings);

    #endregion

    #region SSH Operations (delegated to IGatewaySshService)

    /// <summary>
    /// Test SSH connection to the gateway for a site
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(int siteId)
        => _gatewaySsh.TestConnectionAsync(siteId);

    /// <summary>
    /// Run an SSH command on the gateway for a site
    /// </summary>
    public Task<(bool success, string output)> RunSshCommandAsync(int siteId, string command)
        => _gatewaySsh.RunCommandAsync(siteId, command);

    #endregion

    #region iperf3 Operations

    /// <summary>
    /// Check if iperf3 is running on the gateway for a site and get its port
    /// </summary>
    public async Task<Iperf3Status> CheckIperf3StatusAsync(int siteId)
    {
        var settings = await GetSettingsAsync(siteId);
        var status = new Iperf3Status();

        if (string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
        {
            status.Error = "Gateway SSH not configured";
            return status;
        }

        try
        {
            // First verify SSH connection works
            var connectTest = await RunSshCommandAsync(siteId, "echo SSH_OK");
            if (!connectTest.success || !connectTest.output.Contains("SSH_OK"))
            {
                status.Error = $"SSH connection failed: {connectTest.output}";
                return status;
            }

            // Check if iperf3 is running
            var result = await RunSshCommandAsync(siteId, "pgrep -a iperf3 2>/dev/null || true");
            if (result.success && !string.IsNullOrWhiteSpace(result.output))
            {
                status.IsRunning = true;

                // Try to extract the port from the command line
                var portMatch = Regex.Match(result.output, @"-p\s*(\d+)");
                if (portMatch.Success)
                {
                    status.Port = int.Parse(portMatch.Groups[1].Value);
                }
                else
                {
                    // Check if running on default port
                    status.Port = 5201;
                }

                // Also try to get the port from netstat/ss
                var netstatResult = await RunSshCommandAsync(siteId, "ss -tlnp 2>/dev/null | grep iperf3 || true");
                if (netstatResult.success && !string.IsNullOrWhiteSpace(netstatResult.output))
                {
                    // Parse something like "*:5201" or "0.0.0.0:5201"
                    var listenMatch = Regex.Match(netstatResult.output, @":(\d+)\s");
                    if (listenMatch.Success)
                    {
                        status.Port = int.Parse(listenMatch.Groups[1].Value);
                    }
                }
            }

            // Check if iperf3 is installed
            var versionResult = await RunSshCommandAsync(siteId, "iperf3 --version 2>&1 | head -1");
            if (versionResult.success && versionResult.output.ToLower().Contains("iperf"))
            {
                status.IsInstalled = true;
                status.Version = versionResult.output.Trim();
            }

            // Try to find the service name
            var serviceResult = await RunSshCommandAsync(siteId, "systemctl list-units --type=service --all 2>/dev/null | grep -i iperf || true");
            if (serviceResult.success && !string.IsNullOrWhiteSpace(serviceResult.output))
            {
                // Extract service name from output like "iperf3.service loaded active running"
                var serviceMatch = Regex.Match(serviceResult.output, @"(\S*iperf\S*\.service)");
                if (serviceMatch.Success)
                {
                    status.ServiceName = serviceMatch.Groups[1].Value;
                }
            }

            return status;
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
            return status;
        }
    }

    /// <summary>
    /// Start iperf3 server on the gateway for a site
    /// </summary>
    public async Task<(bool success, string message)> StartIperf3ServerAsync(int siteId, int? port = null)
    {
        var settings = await GetSettingsAsync(siteId);
        var targetPort = port ?? settings.Iperf3Port;

        // First check current status
        var status = await CheckIperf3StatusAsync(siteId);

        // Check for SSH connection errors first
        if (!string.IsNullOrEmpty(status.Error))
        {
            return (false, status.Error);
        }

        if (status.IsRunning)
        {
            return (true, $"iperf3 already running on port {status.Port}");
        }

        if (!status.IsInstalled)
        {
            return (false, "iperf3 is not installed on the gateway");
        }

        // Try to start via systemctl first if service exists
        if (!string.IsNullOrEmpty(status.ServiceName))
        {
            var serviceResult = await RunSshCommandAsync(siteId, $"systemctl start {status.ServiceName} 2>&1");
            if (serviceResult.success)
            {
                await Task.Delay(500); // Wait for service to start
                var newStatus = await CheckIperf3StatusAsync(siteId);
                if (newStatus.IsRunning)
                {
                    return (true, $"Started {status.ServiceName} on port {newStatus.Port}");
                }
            }
        }

        // Try to start iperf3 directly in server daemon mode
        var startResult = await RunSshCommandAsync(siteId, $"nohup iperf3 -s -p {targetPort} -D 2>&1");
        if (startResult.success)
        {
            await Task.Delay(500);
            var newStatus = await CheckIperf3StatusAsync(siteId);
            if (newStatus.IsRunning)
            {
                return (true, $"Started iperf3 server on port {targetPort}");
            }
        }

        return (false, $"Failed to start iperf3: {startResult.output}");
    }

    /// <summary>
    /// Run a speed test from the Docker container to the gateway using system settings for a site
    /// </summary>
    public async Task<GatewaySpeedTestResult> RunSpeedTestAsync(int siteId)
    {
        var iperf3Settings = await _systemSettings.GetIperf3SettingsAsync();
        return await RunSpeedTestAsync(siteId, iperf3Settings.DurationSeconds, iperf3Settings.GatewayParallelStreams);
    }

    /// <summary>
    /// Run a speed test from the Docker container to the gateway with specific parameters for a site
    /// </summary>
    public async Task<GatewaySpeedTestResult> RunSpeedTestAsync(int siteId, int durationSeconds, int parallelStreams)
    {
        if (_isTestRunning.TryGetValue(siteId, out var isRunning) && isRunning)
        {
            return new GatewaySpeedTestResult
            {
                Success = false,
                Error = "A speed test is already running for this site"
            };
        }

        _isTestRunning[siteId] = true;
        var result = new GatewaySpeedTestResult
        {
            TestTime = DateTime.UtcNow,
            DurationSeconds = durationSeconds,
            ParallelStreams = parallelStreams
        };

        try
        {
            var settings = await GetSettingsAsync(siteId);

            if (string.IsNullOrEmpty(settings.Host))
            {
                result.Error = "Gateway host not configured";
                return result;
            }

            // Ensure iperf3 server is running
            var status = await CheckIperf3StatusAsync(siteId);
            if (!status.IsRunning)
            {
                var startResult = await StartIperf3ServerAsync(siteId);
                if (!startResult.success)
                {
                    result.Error = startResult.message;
                    return result;
                }
                // Refresh status
                status = await CheckIperf3StatusAsync(siteId);
            }

            var port = status.Port ?? settings.Iperf3Port;
            result.GatewayHost = settings.Host;
            result.Port = port;

            // Run download test (from gateway to container)
            _logger.LogInformation("Running download test to {Host}:{Port} for site {SiteId}", settings.Host, port, siteId);
            var downloadResult = await RunIperf3ClientAsync(settings.Host, port, durationSeconds, parallelStreams, reverse: true);
            if (downloadResult.success)
            {
                ParseIperf3Result(downloadResult.output, result, isDownload: true);
            }
            else
            {
                result.Error = $"Download test failed: {downloadResult.output}";
                return result;
            }

            // Brief pause between tests
            await Task.Delay(1000);

            // Run upload test (from container to gateway)
            _logger.LogInformation("Running upload test to {Host}:{Port} for site {SiteId}", settings.Host, port, siteId);
            var uploadResult = await RunIperf3ClientAsync(settings.Host, port, durationSeconds, parallelStreams, reverse: false);
            if (uploadResult.success)
            {
                ParseIperf3Result(uploadResult.output, result, isDownload: false);
            }
            else
            {
                result.Error = $"Upload test failed: {uploadResult.output}";
                return result;
            }

            result.Success = true;
            _lastResult[siteId] = result;

            // Analyze network path before saving (use LocalIp parsed from iperf3 output)
            var pathAnalysis = await AnalyzePathAsync(
                siteId,
                settings.Host,
                result.DownloadMbps,
                result.UploadMbps,
                result.DownloadRetransmits,
                result.UploadRetransmits,
                result.DownloadBytes,
                result.UploadBytes,
                result.LocalIp);

            // Save to history database
            await SaveResultToHistoryAsync(siteId, result, pathAnalysis);

            _logger.LogInformation("Speed test completed for site {SiteId}: {FromDevice:F1} Mbps from / {ToDevice:F1} Mbps to device",
                siteId, result.DownloadMbps, result.UploadMbps);

            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "Error running speed test for site {SiteId}", siteId);
            return result;
        }
        finally
        {
            _isTestRunning[siteId] = false;
        }
    }

    private async Task<(bool success, string output)> RunIperf3ClientAsync(
        string host, int port, int duration, int parallel, bool reverse)
    {
        var args = new List<string>
        {
            "-c", host,
            "-p", port.ToString(),
            "-t", duration.ToString(),
            "-P", parallel.ToString(),
            "-J" // JSON output
        };

        if (reverse)
        {
            args.Add("-R"); // Reverse mode for download test
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ProcessUtilities.GetIperf3Path(),
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            // Allow extra time for the test plus overhead
            var timeoutSeconds = duration + 30;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return (false, "iperf3 test timed out");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                return (false, string.IsNullOrEmpty(error) ? output : error);
            }

            return (true, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "iperf3 client execution failed for {Host}:{Port}", host, port);
            return (false, ex.Message);
        }
    }

    private void ParseIperf3Result(string jsonOutput, GatewaySpeedTestResult result, bool isDownload)
    {
        // Store raw JSON for debugging
        if (isDownload)
        {
            result.RawDownloadJson = jsonOutput;
        }
        else
        {
            result.RawUploadJson = jsonOutput;
        }

        // For download tests (reverse mode), prefer sum_received for accurate received bytes
        var parsed = Iperf3JsonParser.Parse(jsonOutput, useSumReceived: isDownload, _logger);

        // Extract local IP (only need to do this once)
        if (string.IsNullOrEmpty(result.LocalIp) && !string.IsNullOrEmpty(parsed.LocalIp))
        {
            result.LocalIp = parsed.LocalIp;
        }

        // Apply results (ignore errors - they're logged by the parser)
        if (isDownload)
        {
            result.DownloadBitsPerSecond = parsed.BitsPerSecond;
            result.DownloadBytes = parsed.Bytes;
            result.DownloadRetransmits = parsed.Retransmits;
        }
        else
        {
            result.UploadBitsPerSecond = parsed.BitsPerSecond;
            result.UploadBytes = parsed.Bytes;
            result.UploadRetransmits = parsed.Retransmits;
        }
    }

    /// <summary>
    /// Analyze the network path to the gateway and calculate efficiency grades
    /// </summary>
    private async Task<PathAnalysisResult?> AnalyzePathAsync(
        int siteId,
        string targetHost,
        double downloadMbps,
        double uploadMbps,
        int downloadRetransmits = 0,
        int uploadRetransmits = 0,
        long downloadBytes = 0,
        long uploadBytes = 0,
        string? localIp = null)
    {
        try
        {
            _logger.LogDebug("Analyzing network path for site {SiteId} to gateway {Host}", siteId, targetHost);

            var pathAnalyzer = GetPathAnalyzer(siteId);
            var path = await pathAnalyzer.CalculatePathAsync(targetHost, localIp);
            var analysis = pathAnalyzer.AnalyzeSpeedTest(
                path,
                downloadMbps,
                uploadMbps,
                downloadRetransmits,
                uploadRetransmits,
                downloadBytes,
                uploadBytes);

            if (analysis.Path.IsValid)
            {
                _logger.LogInformation("Gateway path analysis: {Hops} hops, theoretical max {MaxMbps} Mbps, " +
                    "from-device efficiency {FromEff:F0}% ({FromGrade}), to-device efficiency {ToEff:F0}% ({ToGrade})",
                    analysis.Path.Hops.Count,
                    analysis.Path.TheoreticalMaxMbps,
                    analysis.FromDeviceEfficiencyPercent,
                    analysis.FromDeviceGrade,
                    analysis.ToDeviceEfficiencyPercent,
                    analysis.ToDeviceGrade);
            }
            else
            {
                _logger.LogDebug("Gateway path analysis incomplete: {Error}", analysis.Path.ErrorMessage);
            }

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze network path to gateway {Host}", targetHost);
            return null;
        }
    }

    /// <summary>
    /// Save the gateway speed test result to the shared history database for a site
    /// </summary>
    private async Task SaveResultToHistoryAsync(int siteId, GatewaySpeedTestResult result, PathAnalysisResult? pathAnalysis)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

            var historyResult = new Iperf3Result
            {
                DeviceHost = result.GatewayHost ?? "gateway",
                DeviceName = "Gateway",
                DeviceType = "Gateway",
                TestTime = result.TestTime,
                DurationSeconds = result.DurationSeconds,
                ParallelStreams = result.ParallelStreams,
                Success = result.Success,
                ErrorMessage = result.Error,
                UploadBitsPerSecond = result.UploadBitsPerSecond,
                UploadBytes = result.UploadBytes,
                UploadRetransmits = result.UploadRetransmits,
                DownloadBitsPerSecond = result.DownloadBitsPerSecond,
                DownloadBytes = result.DownloadBytes,
                DownloadRetransmits = result.DownloadRetransmits,
                RawUploadJson = result.RawUploadJson,
                RawDownloadJson = result.RawDownloadJson,
                PathAnalysis = pathAnalysis
            };

            await repository.SaveIperf3ResultAsync(siteId, historyResult);

            _logger.LogDebug("Saved gateway speed test result to history for site {SiteId}", siteId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save gateway speed test result to history for site {SiteId}", siteId);
        }
    }

    /// <summary>
    /// Get the last speed test result for a site
    /// </summary>
    public GatewaySpeedTestResult? GetLastResult(int siteId)
        => _lastResult.TryGetValue(siteId, out var result) ? result : null;

    /// <summary>
    /// Check if a test is currently running for a site
    /// </summary>
    public bool IsTestRunning(int siteId)
        => _isTestRunning.TryGetValue(siteId, out var isRunning) && isRunning;

    #endregion
}

/// <summary>
/// Status of iperf3 on the gateway
/// </summary>
public class Iperf3Status
{
    public bool IsInstalled { get; set; }
    public bool IsRunning { get; set; }
    public int? Port { get; set; }
    public string? Version { get; set; }
    public string? ServiceName { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Result of a gateway speed test
/// </summary>
public class GatewaySpeedTestResult
{
    public DateTime TestTime { get; set; }
    public string? GatewayHost { get; set; }
    public int Port { get; set; }
    public int DurationSeconds { get; set; }
    public int ParallelStreams { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }

    public double DownloadBitsPerSecond { get; set; }
    public long DownloadBytes { get; set; }
    public int DownloadRetransmits { get; set; }

    public double UploadBitsPerSecond { get; set; }
    public long UploadBytes { get; set; }
    public int UploadRetransmits { get; set; }

    public string? RawDownloadJson { get; set; }
    public string? RawUploadJson { get; set; }

    /// <summary>
    /// Local IP address used for the test (parsed from iperf3 output)
    /// </summary>
    public string? LocalIp { get; set; }

    // Computed properties
    public double DownloadMbps => DownloadBitsPerSecond / 1_000_000;
    public double UploadMbps => UploadBitsPerSecond / 1_000_000;
}
