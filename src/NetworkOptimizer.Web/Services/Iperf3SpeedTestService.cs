using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running iperf3 speed tests to UniFi devices.
/// Uses UniFiSshService for SSH operations with shared credentials.
/// </summary>
public class Iperf3SpeedTestService : IIperf3SpeedTestService
{
    private readonly ILogger<Iperf3SpeedTestService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiSshService _sshService;
    private readonly SystemSettingsService _settingsService;
    private readonly UniFiConnectionService _connectionService;
    private readonly IMemoryCache _cache;
    private readonly ILoggerFactory _loggerFactory;

    // Track running tests per site to prevent duplicates
    private readonly ConcurrentDictionary<int, HashSet<string>> _runningTests = new();
    private readonly object _lock = new();

    // Default iperf3 port
    private const int Iperf3Port = 5201;

    // Cache detected OS per host to avoid repeated checks (keyed by siteId:host)
    private readonly ConcurrentDictionary<string, bool> _isWindowsCache = new();

    // Cache iperf3 path per host (for Windows with paths containing spaces, keyed by siteId:host)
    private readonly ConcurrentDictionary<string, string> _iperf3PathCache = new();

    // Site-specific path analyzers
    private readonly ConcurrentDictionary<int, INetworkPathAnalyzer> _pathAnalyzers = new();

    public Iperf3SpeedTestService(
        ILogger<Iperf3SpeedTestService> logger,
        IServiceProvider serviceProvider,
        UniFiSshService sshService,
        SystemSettingsService settingsService,
        UniFiConnectionService connectionService,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshService = sshService;
        _settingsService = settingsService;
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

    /// <summary>
    /// Get iperf3 test settings
    /// </summary>
    public Task<Iperf3Settings> GetSettingsAsync(int siteId) => _settingsService.GetIperf3SettingsAsync();

    /// <summary>
    /// Get all configured devices (delegates to UniFiSshService)
    /// </summary>
    public Task<List<DeviceSshConfiguration>> GetDevicesAsync(int siteId) => _sshService.GetDevicesAsync(siteId);

    /// <summary>
    /// Save a device (delegates to UniFiSshService)
    /// </summary>
    public Task<DeviceSshConfiguration> SaveDeviceAsync(int siteId, DeviceSshConfiguration device) => _sshService.SaveDeviceAsync(siteId, device);

    /// <summary>
    /// Delete a device (delegates to UniFiSshService)
    /// </summary>
    public Task DeleteDeviceAsync(int siteId, int id) => _sshService.DeleteDeviceAsync(siteId, id);

    /// <summary>
    /// Test SSH connection to a device (using global credentials)
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(int siteId, string host) => _sshService.TestConnectionAsync(siteId, host);

    /// <summary>
    /// Test SSH connection to a device (using device-specific credentials if configured)
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(int siteId, DeviceSshConfiguration device) => _sshService.TestConnectionAsync(siteId, device);

    /// <summary>
    /// Check if iperf3 is available on a device (using global credentials)
    /// </summary>
    public Task<(bool available, string version)> CheckIperf3AvailableAsync(int siteId, string host) => _sshService.CheckToolAvailableAsync(siteId, host, "iperf3");

    /// <summary>
    /// Check if iperf3 is available on a device (using device-specific credentials if configured)
    /// </summary>
    public Task<(bool available, string version)> CheckIperf3AvailableAsync(int siteId, DeviceSshConfiguration device)
    {
        // Use custom binary path if configured, otherwise default to "iperf3"
        var iperf3Bin = !string.IsNullOrWhiteSpace(device.Iperf3BinaryPath)
            ? device.Iperf3BinaryPath
            : "iperf3";
        return _sshService.CheckToolAvailableAsync(siteId, device, iperf3Bin);
    }

    /// <summary>
    /// Get the cache key for per-site host tracking
    /// </summary>
    private static string GetCacheKey(int siteId, string host) => $"{siteId}:{host}";

    /// <summary>
    /// Detect if the remote host is running Windows
    /// </summary>
    private async Task<bool> IsWindowsHostAsync(int siteId, DeviceSshConfiguration device)
    {
        var cacheKey = GetCacheKey(siteId, device.Host);
        if (_isWindowsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Try uname -s first (works on Linux/macOS/Unix)
        var unameResult = await _sshService.RunCommandWithDeviceAsync(siteId, device, "uname -s 2>/dev/null");
        if (unameResult.success)
        {
            var os = unameResult.output.Trim().ToLowerInvariant();
            if (os.Contains("linux") || os.Contains("darwin") || os.Contains("freebsd") || os.Contains("unix"))
            {
                _isWindowsCache[cacheKey] = false;
                _logger.LogInformation("Detected {Host} as Linux/Unix", device.Host);
                return false;
            }
        }

        // Check for Windows by testing pwsh availability (pwsh comes with Windows SSH)
        var pwshCheck = await _sshService.RunCommandWithDeviceAsync(siteId, device, "pwsh -Version 2>nul");
        var isWindows = pwshCheck.success && pwshCheck.output.Contains("PowerShell");

        _isWindowsCache[cacheKey] = isWindows;
        _logger.LogInformation("Detected {Host} as {OS}", device.Host, isWindows ? "Windows" : "Linux/Unix");
        return isWindows;
    }

    /// <summary>
    /// Kill iperf3 processes on the remote host
    /// </summary>
    private async Task KillIperf3Async(int siteId, DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Use taskkill directly - simpler and more reliable
            await _sshService.RunCommandWithDeviceAsync(siteId, device, "taskkill /F /IM iperf3.exe 2>nul || echo done");
        }
        else
        {
            await _sshService.RunCommandWithDeviceAsync(siteId, device, "pkill -9 iperf3 2>/dev/null || true");
        }
    }

    /// <summary>
    /// Get the full path to iperf3 on Windows (needed for WMI when path contains spaces)
    /// </summary>
    private async Task<string?> GetWindowsIperf3PathAsync(int siteId, DeviceSshConfiguration device)
    {
        var cacheKey = GetCacheKey(siteId, device.Host);
        if (_iperf3PathCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var result = await _sshService.RunCommandWithDeviceAsync(siteId, device, "where iperf3 2>nul");
        if (result.success && !string.IsNullOrWhiteSpace(result.output))
        {
            // Take first line (in case multiple are found)
            var path = result.output.Split('\n', '\r')[0].Trim();
            if (!string.IsNullOrEmpty(path))
            {
                _iperf3PathCache[cacheKey] = path;
                return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Start iperf3 server on the remote host (one-shot mode)
    /// </summary>
    private async Task<(bool success, string output)> StartIperf3ServerAsync(int siteId, DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Use configured path if set, otherwise find iperf3 in PATH
            var iperf3Path = !string.IsNullOrWhiteSpace(device.Iperf3BinaryPath)
                ? device.Iperf3BinaryPath
                : await GetWindowsIperf3PathAsync(siteId, device);

            if (string.IsNullOrEmpty(iperf3Path))
            {
                return (false, "iperf3 not found. Install iperf3 and ensure it's in the system PATH, or configure a custom path.");
            }

            // Use WMI to create a detached process that survives SSH session end
            // Quote the path to handle spaces (e.g., "C:\Program Files\iperf3\iperf3.exe")
            var cmd = $"pwsh -Command \"$r = Invoke-WmiMethod -Class Win32_Process -Name Create -ArgumentList '\\\"{iperf3Path}\\\" -s -p {Iperf3Port}'; if ($r.ReturnValue -eq 0) {{ 'started:' + $r.ProcessId }} else {{ 'failed:' + $r.ReturnValue }}\"";
            return await _sshService.RunCommandWithDeviceAsync(siteId, device, cmd);
        }
        else
        {
            // Use configured path if set, otherwise default to "iperf3" from PATH
            var iperf3Bin = !string.IsNullOrWhiteSpace(device.Iperf3BinaryPath)
                ? device.Iperf3BinaryPath
                : "iperf3";
            var cmd = $"nohup {iperf3Bin} -s -p {Iperf3Port} > /tmp/iperf3_server.log 2>&1 & echo $!";
            return await _sshService.RunCommandWithDeviceAsync(siteId, device, cmd);
        }
    }

    /// <summary>
    /// Check if iperf3 server is running on the remote host
    /// </summary>
    private async Task<bool> IsIperf3ServerRunningAsync(int siteId, DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Use tasklist to check if iperf3 is running - output process list for better debugging
            var result = await _sshService.RunCommandWithDeviceAsync(siteId, device,
                "tasklist /FI \"IMAGENAME eq iperf3.exe\" 2>nul");
            _logger.LogDebug("Windows tasklist output for iperf3: {Output}", result.output);

            // tasklist shows the process info if found, or "INFO: No tasks are running..." if not
            var isRunning = result.success && result.output.Contains("iperf3", StringComparison.OrdinalIgnoreCase)
                && !result.output.Contains("No tasks", StringComparison.OrdinalIgnoreCase);

            if (!isRunning)
            {
                // Double-check with netstat for port listening
                var portCheck = await _sshService.RunCommandWithDeviceAsync(siteId, device,
                    $"netstat -an | findstr \":{Iperf3Port}\" | findstr LISTENING");
                _logger.LogDebug("Windows netstat output for port {Port}: {Output}", Iperf3Port, portCheck.output);
                isRunning = portCheck.success && portCheck.output.Contains("LISTENING");
            }

            return isRunning;
        }
        else
        {
            var result = await _sshService.RunCommandWithDeviceAsync(siteId, device, "pgrep -x iperf3 > /dev/null 2>&1 && echo 'running' || echo 'stopped'");
            if (result.output.Contains("running"))
                return true;

            // Double-check with netstat/ss
            var portCheck = await _sshService.RunCommandWithDeviceAsync(siteId, device,
                $"netstat -tln 2>/dev/null | grep -q ':{Iperf3Port}' && echo 'listening' || ss -tln 2>/dev/null | grep -q ':{Iperf3Port}' && echo 'listening' || echo 'not_listening'");
            return portCheck.output.Contains("listening");
        }
    }

    /// <summary>
    /// Get iperf3 server log from the remote host
    /// </summary>
    private async Task<string> GetIperf3ServerLogAsync(int siteId, DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Try to get more helpful info about what went wrong
            var checkIperf3 = await _sshService.RunCommandWithDeviceAsync(siteId, device, "where iperf3 2>nul || echo NOT_FOUND");
            if (checkIperf3.output.Contains("NOT_FOUND"))
            {
                return "iperf3 not found in PATH. Install iperf3 and ensure it's in system PATH.";
            }
            return $"iperf3 found at: {checkIperf3.output.Trim()}. Check that no other process is using port {Iperf3Port}.";
        }
        else
        {
            var result = await _sshService.RunCommandWithDeviceAsync(siteId, device, "cat /tmp/iperf3_server.log 2>/dev/null");
            return result.output;
        }
    }

    /// <summary>
    /// Get or create the running tests set for a site
    /// </summary>
    private HashSet<string> GetRunningTestsForSite(int siteId)
    {
        return _runningTests.GetOrAdd(siteId, _ => new HashSet<string>());
    }

    /// <summary>
    /// Run a full speed test to a device using system settings
    /// </summary>
    public async Task<Iperf3Result> RunSpeedTestAsync(int siteId, DeviceSshConfiguration device)
    {
        var settings = await _settingsService.GetIperf3SettingsAsync();
        var parallelStreams = GetParallelStreamsForDevice(device.DeviceType, settings);
        return await RunSpeedTestAsync(siteId, device, settings.DurationSeconds, parallelStreams);
    }

    /// <summary>
    /// Determine the appropriate parallel streams setting based on device type
    /// </summary>
    private static int GetParallelStreamsForDevice(DeviceType deviceType, Iperf3Settings settings)
    {
        if (deviceType.IsGateway())
            return settings.GatewayParallelStreams;
        if (deviceType.UsesUniFiIperfStreams())
            return settings.UniFiParallelStreams;
        return settings.OtherParallelStreams;
    }

    /// <summary>
    /// Run a full speed test to a device with specific parameters
    /// </summary>
    public async Task<Iperf3Result> RunSpeedTestAsync(int siteId, DeviceSshConfiguration device, int durationSeconds, int parallelStreams)
    {
        var host = device.Host;
        var runningTests = GetRunningTestsForSite(siteId);

        // Check if test is already running for this host in this site
        lock (_lock)
        {
            if (runningTests.Contains(host))
            {
                return new Iperf3Result
                {
                    DeviceHost = host,
                    DeviceName = device.Name,
                    DeviceType = device.DeviceType.ToString(),
                    Success = false,
                    ErrorMessage = "A speed test is already running for this device"
                };
            }
            runningTests.Add(host);
        }

        var result = new Iperf3Result
        {
            DeviceHost = host,
            DeviceName = device.Name,
            DeviceType = device.DeviceType.ToString(),
            TestTime = DateTime.UtcNow,
            DurationSeconds = durationSeconds,
            ParallelStreams = parallelStreams
        };

        // Determine if we should manage the iperf3 server ourselves
        var manageServer = device.StartIperf3Server;
        var isWindows = false;

        try
        {
            _logger.LogInformation("Starting iperf3 speed test to {Device} ({Host})", device.Name, host);

            // Quick connectivity check for saved devices (Id > 0) - skip for UniFi devices
            // which already have UI-level online checks
            if (manageServer && device.Id > 0)
            {
                var (sshOk, sshMsg) = await _sshService.TestConnectionAsync(siteId, device);
                if (!sshOk)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Cannot connect to device: {sshMsg}";
                    _logger.LogWarning("Speed test aborted - SSH connection failed to {Host}: {Message}", host, sshMsg);
                    return result;
                }
            }

            // Refresh topology to get current link speeds before test
            GetPathAnalyzer(siteId).InvalidateTopologyCache();

            // Detect OS if we need to manage the server
            if (manageServer)
            {
                isWindows = await IsWindowsHostAsync(siteId, device);
                _logger.LogDebug("Target {Host} detected as {OS}", host, isWindows ? "Windows" : "Linux/Unix");

                // Step 1: Kill any existing iperf3 server on the device
                _logger.LogDebug("Cleaning up any existing iperf3 processes on {Host}", host);
                await KillIperf3Async(siteId, device, isWindows);

                // Step 2: Start iperf3 server on the remote device
                _logger.LogDebug("Starting iperf3 server on {Host}", host);
                var serverStartResult = await StartIperf3ServerAsync(siteId, device, isWindows);

                if (!serverStartResult.success)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Failed to start iperf3 server: {serverStartResult.output}";
                    return result;
                }

                _logger.LogDebug("iperf3 server start command sent to {Host}, output: {Output}", host, serverStartResult.output);

                // Brief delay to let server start - iperf3 client has 5s connect timeout as fallback
                await Task.Delay(300);
            }
            else
            {
                _logger.LogDebug("Assuming iperf3 server is already running on {Host} (StartIperf3Server=false)", host);
            }

            try
            {
                // Step 3: Run download test (device -> client, with -R flag) - "From Device"
                _logger.LogDebug("Running download test from {Host}", host);
                var downloadResult = await RunLocalIperf3Async(host, durationSeconds, parallelStreams, reverse: true);

                if (downloadResult.success)
                {
                    result.RawDownloadJson = downloadResult.output;
                    ParseIperf3Result(downloadResult.output, result, isUpload: false);
                }
                else
                {
                    _logger.LogWarning("Download test failed: {Error}", downloadResult.output);
                }

                // Brief delay between tests for stability
                await Task.Delay(500);

                // Step 4: Run upload test (client -> device) - "To Device"
                _logger.LogDebug("Running upload test to {Host}", host);
                var uploadResult = await RunLocalIperf3Async(host, durationSeconds, parallelStreams, reverse: false);

                if (uploadResult.success)
                {
                    result.RawUploadJson = uploadResult.output;
                    ParseIperf3Result(uploadResult.output, result, isUpload: true);
                }
                else
                {
                    _logger.LogWarning("Upload test failed: {Error}", uploadResult.output);
                }

                result.Success = downloadResult.success || uploadResult.success;
                if (!result.Success)
                {
                    result.ErrorMessage = $"Both tests failed. Download: {downloadResult.output}, Upload: {uploadResult.output}";
                }
            }
            finally
            {
                if (manageServer)
                {
                    // Step 5: Clean up - stop iperf3 server
                    _logger.LogDebug("Stopping iperf3 server on {Host}", host);
                    await KillIperf3Async(siteId, device, isWindows);
                }
            }

            // Perform path analysis first - this resolves hostname to IP and finds the client
            await AnalyzePathAsync(siteId, result, host);

            // Copy MAC from path analysis if available (needed for hostname-based tests)
            if (string.IsNullOrEmpty(result.ClientMac) && !string.IsNullOrEmpty(result.PathAnalysis?.Path?.DestinationMac))
            {
                result.ClientMac = result.PathAnalysis.Path.DestinationMac;
            }

            // Enrich with client info (MAC, name, Wi-Fi signal) if target is a UniFi client
            // Don't overwrite DeviceName (SSH tests have name from config), but do capture Wi-Fi/MAC
            await _connectionService.EnrichSpeedTestWithClientInfoAsync(siteId, result, setDeviceName: false, overwriteMac: false);

            // Save result to database
            await SaveResultAsync(siteId, result);

            _logger.LogInformation("Speed test to {Device} completed: {FromDevice:F1} Mbps from / {ToDevice:F1} Mbps to device",
                device.Name, result.DownloadMbps, result.UploadMbps);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running speed test to {Device}", device.Name);
            result.Success = false;
            result.ErrorMessage = ex.Message;

            // Try to clean up if we started the server
            if (manageServer)
            {
                try
                {
                    await KillIperf3Async(siteId, device, isWindows);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogDebug(cleanupEx, "Cleanup error");
                }
            }

            return result;
        }
        finally
        {
            lock (_lock)
            {
                runningTests.Remove(host);
            }
        }
    }

    /// <summary>
    /// Get recent speed test results
    /// </summary>
    public async Task<List<Iperf3Result>> GetRecentResultsAsync(int siteId, int count = 50, int hours = 0)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
        return await repository.GetRecentIperf3ResultsAsync(siteId, count, hours);
    }

    /// <summary>
    /// Get speed test results for a specific device
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsForDeviceAsync(int siteId, string deviceHost, int count = 20)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
        return await repository.GetIperf3ResultsForDeviceAsync(siteId, deviceHost, count);
    }

    /// <summary>
    /// Clear all speed test history
    /// </summary>
    public async Task<int> ClearHistoryAsync(int siteId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
        var results = await repository.GetRecentIperf3ResultsAsync(siteId, int.MaxValue);
        var count = results.Count;
        await repository.ClearIperf3HistoryAsync(siteId);
        return count;
    }

    private async Task SaveResultAsync(int siteId, Iperf3Result result)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
            await repository.SaveIperf3ResultAsync(siteId, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save iperf3 result to database");
        }
    }

    /// <summary>
    /// Gets the path to the local iperf3 executable for client operations.
    /// On Windows, prefers the bundled/installed iperf3 over PATH.
    /// On Linux/macOS, uses iperf3 from PATH.
    /// </summary>
    private static string GetLocalIperf3Path()
    {
        if (OperatingSystem.IsWindows())
        {
            // Look for bundled iperf3 relative to the application directory
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "iperf3", "iperf3.exe");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }
        }

        // Fall back to iperf3 in PATH (Linux/macOS/Docker)
        return "iperf3";
    }

    private async Task<(bool success, string output)> RunLocalIperf3Async(string host, int duration, int streams, bool reverse)
    {
        // --connect-timeout in ms - fail fast if server isn't running (5 second connection timeout)
        var args = $"-c {host} -p {Iperf3Port} -t {duration} -P {streams} -J --connect-timeout 5000";
        if (reverse)
        {
            args += " -R";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = GetLocalIperf3Path(),
            Arguments = args,
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

            // Connection timeout is 5s, so overall timeout can be shorter
            var timeoutMs = (duration + 15) * 1000;
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred
            }

            if (!process.HasExited)
            {
                process.Kill();
                return (false, "iperf3 client timed out");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                return (false, string.IsNullOrEmpty(error) ? output : error);
            }

            // iperf3 may return exit code 0 but have an error in JSON (e.g., connection timeout)
            // Check for error field in JSON output
            if (output.Contains("\"error\""))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    {
                        var errorMsg = errorProp.GetString();
                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            return (false, errorMsg);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // If we can't parse, just return the raw output
                    _logger.LogDebug(ex, "Failed to parse iperf3 error JSON, returning raw output");
                }
            }

            return (true, output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local iperf3 execution failed for {Host}", host);
            return (false, ex.Message);
        }
    }

    private void ParseIperf3Result(string json, Iperf3Result result, bool isUpload)
    {
        // For download tests, prefer sum_received for accurate received bytes
        var parsed = Iperf3JsonParser.Parse(json, useSumReceived: !isUpload, _logger);

        // Extract local IP (only need to do this once)
        if (string.IsNullOrEmpty(result.LocalIp) && !string.IsNullOrEmpty(parsed.LocalIp))
        {
            result.LocalIp = parsed.LocalIp;
        }

        // Handle errors
        if (!string.IsNullOrEmpty(parsed.ErrorMessage))
        {
            if (isUpload)
                result.ErrorMessage = $"Upload error: {parsed.ErrorMessage}";
            else
                result.ErrorMessage = (result.ErrorMessage ?? "") + $" Download error: {parsed.ErrorMessage}";
            return;
        }

        // Apply results
        if (isUpload)
        {
            result.UploadBitsPerSecond = parsed.BitsPerSecond;
            result.UploadBytes = parsed.Bytes;
            result.UploadRetransmits = parsed.Retransmits;
        }
        else
        {
            result.DownloadBitsPerSecond = parsed.BitsPerSecond;
            result.DownloadBytes = parsed.Bytes;
            result.DownloadRetransmits = parsed.Retransmits;
        }
    }

    /// <summary>
    /// Analyze the network path and grade the speed test result
    /// </summary>
    private async Task AnalyzePathAsync(int siteId, Iperf3Result result, string targetHost)
    {
        try
        {
            _logger.LogDebug("Analyzing network path for site {SiteId} to {Host} from {SourceIp}",
                siteId, targetHost, result.LocalIp ?? "auto");

            var pathAnalyzer = GetPathAnalyzer(siteId);
            var path = await pathAnalyzer.CalculatePathAsync(targetHost, result.LocalIp);
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
                _logger.LogInformation("Path analysis: {Hops} hops, theoretical max {MaxMbps} Mbps, " +
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
                _logger.LogDebug("Path analysis incomplete: {Error}", analysis.Path.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze network path to {Host}", targetHost);
            // Don't fail the test - path analysis is optional
        }
    }

}
