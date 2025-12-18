using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for running iperf3 speed tests to UniFi devices.
/// Uses UniFiSshService for SSH operations with shared credentials.
/// </summary>
public class Iperf3SpeedTestService
{
    private readonly ILogger<Iperf3SpeedTestService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniFiSshService _sshService;
    private readonly SystemSettingsService _settingsService;

    // Track running tests to prevent duplicates
    private readonly HashSet<string> _runningTests = new();
    private readonly object _lock = new();

    // Default iperf3 port
    private const int Iperf3Port = 5201;

    // Cache detected OS per host to avoid repeated checks
    private readonly Dictionary<string, bool> _isWindowsCache = new();

    public Iperf3SpeedTestService(
        ILogger<Iperf3SpeedTestService> logger,
        IServiceProvider serviceProvider,
        UniFiSshService sshService,
        SystemSettingsService settingsService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshService = sshService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Get iperf3 test settings
    /// </summary>
    public Task<Iperf3Settings> GetSettingsAsync() => _settingsService.GetIperf3SettingsAsync();

    /// <summary>
    /// Get all configured devices (delegates to UniFiSshService)
    /// </summary>
    public Task<List<DeviceSshConfiguration>> GetDevicesAsync() => _sshService.GetDevicesAsync();

    /// <summary>
    /// Save a device (delegates to UniFiSshService)
    /// </summary>
    public Task<DeviceSshConfiguration> SaveDeviceAsync(DeviceSshConfiguration device) => _sshService.SaveDeviceAsync(device);

    /// <summary>
    /// Delete a device (delegates to UniFiSshService)
    /// </summary>
    public Task DeleteDeviceAsync(int id) => _sshService.DeleteDeviceAsync(id);

    /// <summary>
    /// Test SSH connection to a device (using global credentials)
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(string host) => _sshService.TestConnectionAsync(host);

    /// <summary>
    /// Test SSH connection to a device (using device-specific credentials if configured)
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(DeviceSshConfiguration device) => _sshService.TestConnectionAsync(device);

    /// <summary>
    /// Check if iperf3 is available on a device
    /// </summary>
    public Task<(bool available, string version)> CheckIperf3AvailableAsync(string host) => _sshService.CheckToolAvailableAsync(host, "iperf3");

    /// <summary>
    /// Detect if the remote host is running Windows
    /// </summary>
    private async Task<bool> IsWindowsHostAsync(DeviceSshConfiguration device)
    {
        lock (_lock)
        {
            if (_isWindowsCache.TryGetValue(device.Host, out var cached))
                return cached;
        }

        // Try uname -s first (works on Linux/macOS/Unix)
        var unameResult = await _sshService.RunCommandWithDeviceAsync(device, "uname -s 2>/dev/null");
        if (unameResult.success)
        {
            var os = unameResult.output.Trim().ToLowerInvariant();
            if (os.Contains("linux") || os.Contains("darwin") || os.Contains("freebsd") || os.Contains("unix"))
            {
                lock (_lock) { _isWindowsCache[device.Host] = false; }
                return false;
            }
        }

        // Check for Windows by testing pwsh availability (pwsh comes with Windows SSH)
        var pwshCheck = await _sshService.RunCommandWithDeviceAsync(device, "pwsh -Version 2>nul");
        var isWindows = pwshCheck.success && pwshCheck.output.Contains("PowerShell");

        lock (_lock) { _isWindowsCache[device.Host] = isWindows; }
        _logger.LogInformation("Detected {Host} as {OS}", device.Host, isWindows ? "Windows" : "Linux/Unix");
        return isWindows;
    }

    /// <summary>
    /// Kill iperf3 processes on the remote host
    /// </summary>
    private async Task KillIperf3Async(DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Use taskkill directly - simpler and more reliable
            await _sshService.RunCommandWithDeviceAsync(device, "taskkill /F /IM iperf3.exe 2>nul || echo done");
        }
        else
        {
            await _sshService.RunCommandWithDeviceAsync(device, "pkill -9 iperf3 2>/dev/null || true");
        }
    }

    /// <summary>
    /// Start iperf3 server on the remote host (one-shot mode)
    /// </summary>
    private async Task<(bool success, string output)> StartIperf3ServerAsync(DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Use WMI to create a detached process that survives SSH session end
            // This is the only reliable way to background a process on Windows via SSH
            var cmd = $"pwsh -Command \"$r = Invoke-WmiMethod -Class Win32_Process -Name Create -ArgumentList 'iperf3 -s -1 -p {Iperf3Port}'; if ($r.ReturnValue -eq 0) {{ 'started:' + $r.ProcessId }} else {{ 'failed:' + $r.ReturnValue }}\"";
            return await _sshService.RunCommandWithDeviceAsync(device, cmd);
        }
        else
        {
            var cmd = $"nohup iperf3 -s -1 -p {Iperf3Port} > /tmp/iperf3_server.log 2>&1 & echo $!";
            return await _sshService.RunCommandWithDeviceAsync(device, cmd);
        }
    }

    /// <summary>
    /// Check if iperf3 server is running on the remote host
    /// </summary>
    private async Task<bool> IsIperf3ServerRunningAsync(DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Use tasklist to check if iperf3 is running - output process list for better debugging
            var result = await _sshService.RunCommandWithDeviceAsync(device,
                "tasklist /FI \"IMAGENAME eq iperf3.exe\" 2>nul");
            _logger.LogDebug("Windows tasklist output for iperf3: {Output}", result.output);

            // tasklist shows the process info if found, or "INFO: No tasks are running..." if not
            var isRunning = result.success && result.output.Contains("iperf3", StringComparison.OrdinalIgnoreCase)
                && !result.output.Contains("No tasks", StringComparison.OrdinalIgnoreCase);

            if (!isRunning)
            {
                // Double-check with netstat for port listening
                var portCheck = await _sshService.RunCommandWithDeviceAsync(device,
                    $"netstat -an | findstr \":{Iperf3Port}\" | findstr LISTENING");
                _logger.LogDebug("Windows netstat output for port {Port}: {Output}", Iperf3Port, portCheck.output);
                isRunning = portCheck.success && portCheck.output.Contains("LISTENING");
            }

            return isRunning;
        }
        else
        {
            var result = await _sshService.RunCommandWithDeviceAsync(device, "pgrep -x iperf3 > /dev/null 2>&1 && echo 'running' || echo 'stopped'");
            if (result.output.Contains("running"))
                return true;

            // Double-check with netstat/ss
            var portCheck = await _sshService.RunCommandWithDeviceAsync(device,
                $"netstat -tln 2>/dev/null | grep -q ':{Iperf3Port}' && echo 'listening' || ss -tln 2>/dev/null | grep -q ':{Iperf3Port}' && echo 'listening' || echo 'not_listening'");
            return portCheck.output.Contains("listening");
        }
    }

    /// <summary>
    /// Get iperf3 server log from the remote host
    /// </summary>
    private async Task<string> GetIperf3ServerLogAsync(DeviceSshConfiguration device, bool isWindows)
    {
        if (isWindows)
        {
            // Try to get more helpful info about what went wrong
            var checkIperf3 = await _sshService.RunCommandWithDeviceAsync(device, "where iperf3 2>nul || echo NOT_FOUND");
            if (checkIperf3.output.Contains("NOT_FOUND"))
            {
                return "iperf3 not found in PATH. Install iperf3 and ensure it's in system PATH.";
            }
            return $"iperf3 found at: {checkIperf3.output.Trim()}. Check that no other process is using port {Iperf3Port}.";
        }
        else
        {
            var result = await _sshService.RunCommandWithDeviceAsync(device, "cat /tmp/iperf3_server.log 2>/dev/null");
            return result.output;
        }
    }

    /// <summary>
    /// Run a full speed test to a device using system settings
    /// </summary>
    public async Task<Iperf3Result> RunSpeedTestAsync(DeviceSshConfiguration device)
    {
        var settings = await _settingsService.GetIperf3SettingsAsync();
        return await RunSpeedTestAsync(device, settings.DurationSeconds, settings.ParallelStreams);
    }

    /// <summary>
    /// Run a full speed test to a device with specific parameters
    /// </summary>
    public async Task<Iperf3Result> RunSpeedTestAsync(DeviceSshConfiguration device, int durationSeconds, int parallelStreams)
    {
        var host = device.Host;

        // Check if test is already running for this host
        lock (_lock)
        {
            if (_runningTests.Contains(host))
            {
                return new Iperf3Result
                {
                    DeviceHost = host,
                    DeviceName = device.Name,
                    DeviceType = device.DeviceType,
                    Success = false,
                    ErrorMessage = "A speed test is already running for this device"
                };
            }
            _runningTests.Add(host);
        }

        var result = new Iperf3Result
        {
            DeviceHost = host,
            DeviceName = device.Name,
            DeviceType = device.DeviceType,
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

            // Detect OS if we need to manage the server
            if (manageServer)
            {
                isWindows = await IsWindowsHostAsync(device);
                _logger.LogDebug("Target {Host} detected as {OS}", host, isWindows ? "Windows" : "Linux/Unix");

                // Step 1: Kill any existing iperf3 server on the device
                _logger.LogDebug("Cleaning up any existing iperf3 processes on {Host}", host);
                await KillIperf3Async(device, isWindows);
                await Task.Delay(500);

                // Step 2: Start iperf3 server on the remote device
                _logger.LogDebug("Starting iperf3 server on {Host}", host);
                var serverStartResult = await StartIperf3ServerAsync(device, isWindows);

                if (!serverStartResult.success)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Failed to start iperf3 server: {serverStartResult.output}";
                    return result;
                }

                _logger.LogDebug("iperf3 server start command sent to {Host}, output: {Output}", host, serverStartResult.output);

                // Verify server is running with retries (WMI process creation can be slow)
                var serverRunning = false;
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    // Increase delay for each attempt
                    var delayMs = attempt * 1000;
                    _logger.LogDebug("Waiting {Delay}ms before checking iperf3 server (attempt {Attempt}/5)", delayMs, attempt);
                    await Task.Delay(delayMs);

                    serverRunning = await IsIperf3ServerRunningAsync(device, isWindows);
                    if (serverRunning)
                    {
                        _logger.LogDebug("iperf3 server confirmed running on {Host} after {Attempt} attempt(s)", host, attempt);
                        break;
                    }
                    _logger.LogDebug("iperf3 server not detected on {Host} (attempt {Attempt}/5)", host, attempt);
                }

                if (!serverRunning)
                {
                    var log = await GetIperf3ServerLogAsync(device, isWindows);
                    result.Success = false;
                    result.ErrorMessage = $"iperf3 server failed to start after 5 attempts. {log}";
                    return result;
                }
            }
            else
            {
                _logger.LogDebug("Assuming iperf3 server is already running on {Host} (StartIperf3Server=false)", host);
            }

            try
            {
                // Step 3: Run upload test (client -> device)
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

                if (manageServer)
                {
                    // Server runs with -1 (one-off mode), so restart for download test
                    await KillIperf3Async(device, isWindows);
                    await Task.Delay(500);

                    // Restart server for download test
                    await StartIperf3ServerAsync(device, isWindows);
                    await Task.Delay(1500);
                }

                // Step 4: Run download test (device -> client, with -R flag)
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

                result.Success = uploadResult.success || downloadResult.success;
                if (!result.Success)
                {
                    result.ErrorMessage = $"Both tests failed. Upload: {uploadResult.output}, Download: {downloadResult.output}";
                }
            }
            finally
            {
                if (manageServer)
                {
                    // Step 5: Clean up - stop iperf3 server
                    _logger.LogDebug("Stopping iperf3 server on {Host}", host);
                    await KillIperf3Async(device, isWindows);
                }
            }

            // Save result to database
            await SaveResultAsync(result);

            _logger.LogInformation("Speed test to {Device} completed: Upload={Upload:F1} Mbps, Download={Download:F1} Mbps",
                device.Name, result.UploadMbps, result.DownloadMbps);

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
                    await KillIperf3Async(device, isWindows);
                }
                catch { }
            }

            return result;
        }
        finally
        {
            lock (_lock)
            {
                _runningTests.Remove(host);
            }
        }
    }

    /// <summary>
    /// Get recent speed test results
    /// </summary>
    public async Task<List<Iperf3Result>> GetRecentResultsAsync(int count = 50)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        return await db.Iperf3Results
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Get speed test results for a specific device
    /// </summary>
    public async Task<List<Iperf3Result>> GetResultsForDeviceAsync(string deviceHost, int count = 20)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        return await db.Iperf3Results
            .Where(r => r.DeviceHost == deviceHost)
            .OrderByDescending(r => r.TestTime)
            .Take(count)
            .ToListAsync();
    }

    /// <summary>
    /// Clear all speed test history
    /// </summary>
    public async Task<int> ClearHistoryAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        var count = await db.Iperf3Results.CountAsync();
        db.Iperf3Results.RemoveRange(db.Iperf3Results);
        await db.SaveChangesAsync();
        return count;
    }

    private async Task SaveResultAsync(Iperf3Result result)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            db.Iperf3Results.Add(result);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save iperf3 result to database");
        }
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
            FileName = "iperf3",
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
            var completed = await Task.WhenAny(
                Task.Run(() => process.WaitForExit(timeoutMs)),
                Task.Delay(timeoutMs)
            );

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
                catch
                {
                    // If we can't parse, just return the raw output
                }
            }

            return (true, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private void ParseIperf3Result(string json, Iperf3Result result, bool isUpload)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString();
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    if (isUpload)
                        result.ErrorMessage = $"Upload error: {errorMsg}";
                    else
                        result.ErrorMessage = (result.ErrorMessage ?? "") + $" Download error: {errorMsg}";
                    return;
                }
            }

            if (root.TryGetProperty("end", out var end))
            {
                if (end.TryGetProperty("sum_sent", out var sumSent))
                {
                    var bps = sumSent.GetProperty("bits_per_second").GetDouble();
                    var bytes = sumSent.GetProperty("bytes").GetInt64();
                    var retransmits = 0;
                    if (sumSent.TryGetProperty("retransmits", out var retrans))
                    {
                        retransmits = retrans.GetInt32();
                    }

                    if (isUpload)
                    {
                        result.UploadBitsPerSecond = bps;
                        result.UploadBytes = bytes;
                        result.UploadRetransmits = retransmits;
                    }
                    else
                    {
                        result.DownloadBitsPerSecond = bps;
                        result.DownloadBytes = bytes;
                        result.DownloadRetransmits = retransmits;
                    }
                }

                if (end.TryGetProperty("sum_received", out var sumReceived))
                {
                    var bps = sumReceived.GetProperty("bits_per_second").GetDouble();
                    var bytes = sumReceived.GetProperty("bytes").GetInt64();

                    if (!isUpload)
                    {
                        result.DownloadBitsPerSecond = bps;
                        result.DownloadBytes = bytes;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse iperf3 JSON result");
        }
    }
}
