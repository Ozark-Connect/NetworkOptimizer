using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing gateway SSH settings and running iperf3 speed tests.
/// The gateway typically has different SSH credentials than other UniFi devices.
/// </summary>
public class GatewaySpeedTestService
{
    private readonly ILogger<GatewaySpeedTestService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiConnectionService _connectionService;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly SystemSettingsService _systemSettings;
    private readonly NetworkPathAnalyzer _pathAnalyzer;

    // Cache the settings to avoid repeated DB queries
    private GatewaySshSettings? _cachedSettings;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    // Track running tests
    private bool _isTestRunning = false;
    private GatewaySpeedTestResult? _lastResult;

    public GatewaySpeedTestService(
        ILogger<GatewaySpeedTestService> logger,
        IServiceProvider serviceProvider,
        UniFiConnectionService connectionService,
        SystemSettingsService systemSettings,
        ICredentialProtectionService credentialProtection,
        NetworkPathAnalyzer pathAnalyzer)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _connectionService = connectionService;
        _credentialProtection = credentialProtection;
        _systemSettings = systemSettings;
        _pathAnalyzer = pathAnalyzer;
    }

    #region Settings Management

    /// <summary>
    /// Get the gateway SSH settings (creates default if none exist)
    /// </summary>
    public async Task<GatewaySshSettings> GetSettingsAsync()
    {
        // Check cache first
        if (_cachedSettings != null && DateTime.UtcNow - _cacheTime < _cacheExpiry)
        {
            return _cachedSettings;
        }

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        var settings = await repository.GetGatewaySshSettingsAsync();

        if (settings == null)
        {
            // Create default settings, try to get gateway host from connection service
            var gatewayHost = GetGatewayHostFromController();

            settings = new GatewaySshSettings
            {
                Host = gatewayHost,
                Username = "root",
                Port = 22,
                Iperf3Port = 5201,
                Enabled = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await repository.SaveGatewaySshSettingsAsync(settings);
        }

        _cachedSettings = settings;
        _cacheTime = DateTime.UtcNow;

        return settings;
    }

    /// <summary>
    /// Save gateway SSH settings
    /// </summary>
    public async Task<GatewaySshSettings> SaveSettingsAsync(GatewaySshSettings settings)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();

        settings.UpdatedAt = DateTime.UtcNow;

        // Encrypt password if provided and not already encrypted
        if (!string.IsNullOrEmpty(settings.Password) && !_credentialProtection.IsEncrypted(settings.Password))
        {
            settings.Password = _credentialProtection.Encrypt(settings.Password);
        }

        await repository.SaveGatewaySshSettingsAsync(settings);

        // Invalidate cache
        _cachedSettings = null;

        return settings;
    }

    private string? GetGatewayHostFromController()
    {
        if (_connectionService.CurrentConfig != null)
        {
            // Extract host from controller URL
            try
            {
                var uri = new Uri(_connectionService.CurrentConfig.ControllerUrl);
                return uri.Host;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    #endregion

    #region SSH Operations

    /// <summary>
    /// Test SSH connection to the gateway
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync()
    {
        var settings = await GetSettingsAsync();

        if (string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway host not configured");
        }

        if (!settings.HasCredentials)
        {
            return (false, "SSH credentials not configured");
        }

        try
        {
            var result = await RunSshCommandAsync("echo 'Connection successful'");
            if (result.success && result.output.Contains("Connection successful"))
            {
                // Update last tested
                settings.LastTestedAt = DateTime.UtcNow;
                settings.LastTestResult = "Success";
                await SaveSettingsAsync(settings);

                return (true, "SSH connection successful");
            }
            return (false, result.output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Run an SSH command on the gateway
    /// </summary>
    public async Task<(bool success, string output)> RunSshCommandAsync(string command)
    {
        var settings = await GetSettingsAsync();

        if (string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway host not configured");
        }

        if (!settings.HasCredentials)
        {
            return (false, "SSH credentials not configured");
        }

        var usePassword = !string.IsNullOrEmpty(settings.Password) && string.IsNullOrEmpty(settings.PrivateKeyPath);

        var sshArgs = new List<string>
        {
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "ConnectTimeout=10"
        };

        if (!usePassword)
        {
            sshArgs.Add("-o");
            sshArgs.Add("BatchMode=yes");
        }

        sshArgs.Add("-p");
        sshArgs.Add(settings.Port.ToString());

        if (!string.IsNullOrEmpty(settings.PrivateKeyPath))
        {
            sshArgs.Add("-i");
            sshArgs.Add(settings.PrivateKeyPath);
        }

        sshArgs.Add($"{settings.Username}@{settings.Host}");
        sshArgs.Add(command);

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (usePassword)
        {
            var decryptedPassword = _credentialProtection.Decrypt(settings.Password!);
            startInfo.FileName = "sshpass";
            startInfo.Arguments = $"-e ssh {string.Join(" ", sshArgs)}";
            startInfo.Environment["SSHPASS"] = decryptedPassword;
        }
        else
        {
            startInfo.FileName = "ssh";
            startInfo.Arguments = string.Join(" ", sshArgs);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAny(
                Task.Run(() => process.WaitForExit(30000)),
                Task.Delay(30000)
            );

            if (!process.HasExited)
            {
                process.Kill();
                return (false, "SSH command timed out");
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
            return (false, ex.Message);
        }
    }

    #endregion

    #region iperf3 Operations

    /// <summary>
    /// Check if iperf3 is running on the gateway and get its port
    /// </summary>
    public async Task<Iperf3Status> CheckIperf3StatusAsync()
    {
        var settings = await GetSettingsAsync();
        var status = new Iperf3Status();

        if (string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
        {
            status.Error = "Gateway SSH not configured";
            return status;
        }

        try
        {
            // Check if iperf3 is running
            var result = await RunSshCommandAsync("pgrep -a iperf3 2>/dev/null || true");
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
                var netstatResult = await RunSshCommandAsync("ss -tlnp 2>/dev/null | grep iperf3 || true");
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
            var versionResult = await RunSshCommandAsync("iperf3 --version 2>&1 | head -1");
            if (versionResult.success && versionResult.output.ToLower().Contains("iperf"))
            {
                status.IsInstalled = true;
                status.Version = versionResult.output.Trim();
            }

            // Try to find the service name
            var serviceResult = await RunSshCommandAsync("systemctl list-units --type=service --all 2>/dev/null | grep -i iperf || true");
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
    /// Start iperf3 server on the gateway
    /// </summary>
    public async Task<(bool success, string message)> StartIperf3ServerAsync(int? port = null)
    {
        var settings = await GetSettingsAsync();
        var targetPort = port ?? settings.Iperf3Port;

        // First check current status
        var status = await CheckIperf3StatusAsync();

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
            var serviceResult = await RunSshCommandAsync($"systemctl start {status.ServiceName} 2>&1");
            if (serviceResult.success)
            {
                await Task.Delay(500); // Wait for service to start
                var newStatus = await CheckIperf3StatusAsync();
                if (newStatus.IsRunning)
                {
                    return (true, $"Started {status.ServiceName} on port {newStatus.Port}");
                }
            }
        }

        // Try to start iperf3 directly in server daemon mode
        var startResult = await RunSshCommandAsync($"nohup iperf3 -s -p {targetPort} -D 2>&1");
        if (startResult.success)
        {
            await Task.Delay(500);
            var newStatus = await CheckIperf3StatusAsync();
            if (newStatus.IsRunning)
            {
                return (true, $"Started iperf3 server on port {targetPort}");
            }
        }

        return (false, $"Failed to start iperf3: {startResult.output}");
    }

    /// <summary>
    /// Run a speed test from the Docker container to the gateway using system settings
    /// </summary>
    public async Task<GatewaySpeedTestResult> RunSpeedTestAsync()
    {
        var iperf3Settings = await _systemSettings.GetIperf3SettingsAsync();
        return await RunSpeedTestAsync(iperf3Settings.DurationSeconds, iperf3Settings.GatewayParallelStreams);
    }

    /// <summary>
    /// Run a speed test from the Docker container to the gateway with specific parameters
    /// </summary>
    public async Task<GatewaySpeedTestResult> RunSpeedTestAsync(int durationSeconds, int parallelStreams)
    {
        if (_isTestRunning)
        {
            return new GatewaySpeedTestResult
            {
                Success = false,
                Error = "A speed test is already running"
            };
        }

        _isTestRunning = true;
        var result = new GatewaySpeedTestResult
        {
            TestTime = DateTime.UtcNow,
            DurationSeconds = durationSeconds,
            ParallelStreams = parallelStreams
        };

        try
        {
            var settings = await GetSettingsAsync();

            if (string.IsNullOrEmpty(settings.Host))
            {
                result.Error = "Gateway host not configured";
                return result;
            }

            // Ensure iperf3 server is running
            var status = await CheckIperf3StatusAsync();
            if (!status.IsRunning)
            {
                var startResult = await StartIperf3ServerAsync();
                if (!startResult.success)
                {
                    result.Error = startResult.message;
                    return result;
                }
                // Refresh status
                status = await CheckIperf3StatusAsync();
            }

            var port = status.Port ?? settings.Iperf3Port;
            result.GatewayHost = settings.Host;
            result.Port = port;

            // Run download test (from gateway to container)
            _logger.LogInformation("Running download test to {Host}:{Port}", settings.Host, port);
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
            _logger.LogInformation("Running upload test to {Host}:{Port}", settings.Host, port);
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
            _lastResult = result;

            // Analyze network path before saving
            var pathAnalysis = await AnalyzePathAsync(settings.Host, result.DownloadMbps, result.UploadMbps);

            // Save to history database
            await SaveResultToHistoryAsync(result, pathAnalysis);

            _logger.LogInformation("Speed test completed: {FromDevice:F1} Mbps from / {ToDevice:F1} Mbps to device",
                result.DownloadMbps, result.UploadMbps);

            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            _logger.LogError(ex, "Error running speed test");
            return result;
        }
        finally
        {
            _isTestRunning = false;
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
            FileName = "iperf3",
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
            var timeoutMs = (duration + 30) * 1000;
            await Task.WhenAny(
                Task.Run(() => process.WaitForExit(timeoutMs)),
                Task.Delay(timeoutMs)
            );

            if (!process.HasExited)
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
            return (false, ex.Message);
        }
    }

    private void ParseIperf3Result(string jsonOutput, GatewaySpeedTestResult result, bool isDownload)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonOutput);
            var root = doc.RootElement;

            if (root.TryGetProperty("end", out var end))
            {
                // Get sum_sent and sum_received
                if (end.TryGetProperty("sum_sent", out var sumSent))
                {
                    var bitsPerSecond = sumSent.GetProperty("bits_per_second").GetDouble();
                    var bytes = sumSent.GetProperty("bytes").GetInt64();
                    var retransmits = sumSent.TryGetProperty("retransmits", out var rt) ? rt.GetInt32() : 0;

                    if (isDownload)
                    {
                        // In reverse mode, sum_sent is actually the download (server -> client)
                        result.DownloadBitsPerSecond = bitsPerSecond;
                        result.DownloadBytes = bytes;
                        result.DownloadRetransmits = retransmits;
                    }
                    else
                    {
                        result.UploadBitsPerSecond = bitsPerSecond;
                        result.UploadBytes = bytes;
                        result.UploadRetransmits = retransmits;
                    }
                }

                if (end.TryGetProperty("sum_received", out var sumReceived))
                {
                    var bitsPerSecond = sumReceived.GetProperty("bits_per_second").GetDouble();
                    var bytes = sumReceived.GetProperty("bytes").GetInt64();

                    if (isDownload)
                    {
                        // In reverse mode, sum_received is the download received at client
                        result.DownloadBitsPerSecond = bitsPerSecond;
                        result.DownloadBytes = bytes;
                    }
                }
            }

            // Store raw JSON for debugging
            if (isDownload)
            {
                result.RawDownloadJson = jsonOutput;
            }
            else
            {
                result.RawUploadJson = jsonOutput;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse iperf3 JSON output");
        }
    }

    /// <summary>
    /// Analyze the network path to the gateway and calculate efficiency grades
    /// </summary>
    private async Task<PathAnalysisResult?> AnalyzePathAsync(
        string targetHost, double downloadMbps, double uploadMbps)
    {
        try
        {
            _logger.LogDebug("Analyzing network path to gateway {Host}", targetHost);

            var path = await _pathAnalyzer.CalculatePathAsync(targetHost);
            var analysis = _pathAnalyzer.AnalyzeSpeedTest(path, downloadMbps, uploadMbps);

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
    /// Save the gateway speed test result to the shared history database
    /// </summary>
    private async Task SaveResultToHistoryAsync(GatewaySpeedTestResult result, PathAnalysisResult? pathAnalysis)
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

            await repository.SaveIperf3ResultAsync(historyResult);

            _logger.LogDebug("Saved gateway speed test result to history");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save gateway speed test result to history");
        }
    }

    /// <summary>
    /// Get the last speed test result
    /// </summary>
    public GatewaySpeedTestResult? GetLastResult() => _lastResult;

    /// <summary>
    /// Check if a test is currently running
    /// </summary>
    public bool IsTestRunning => _isTestRunning;

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

    // Computed properties
    public double DownloadMbps => DownloadBitsPerSecond / 1_000_000;
    public double UploadMbps => UploadBitsPerSecond / 1_000_000;
}
