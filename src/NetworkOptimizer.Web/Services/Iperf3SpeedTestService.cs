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

    // Track running tests to prevent duplicates
    private readonly HashSet<string> _runningTests = new();
    private readonly object _lock = new();

    // Default iperf3 port
    private const int Iperf3Port = 5201;

    public Iperf3SpeedTestService(
        ILogger<Iperf3SpeedTestService> logger,
        IServiceProvider serviceProvider,
        UniFiSshService sshService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sshService = sshService;
    }

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
    /// Test SSH connection to a device
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(string host) => _sshService.TestConnectionAsync(host);

    /// <summary>
    /// Check if iperf3 is available on a device
    /// </summary>
    public Task<(bool available, string version)> CheckIperf3AvailableAsync(string host) => _sshService.CheckToolAvailableAsync(host, "iperf3");

    /// <summary>
    /// Run a full speed test to a device
    /// </summary>
    public async Task<Iperf3Result> RunSpeedTestAsync(DeviceSshConfiguration device, int durationSeconds = 10, int parallelStreams = 3)
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

        try
        {
            _logger.LogInformation("Starting iperf3 speed test to {Device} ({Host})", device.Name, host);

            // Step 1: Kill any existing iperf3 server on the device
            _logger.LogDebug("Cleaning up any existing iperf3 processes on {Host}", host);
            await _sshService.RunCommandAsync(host, "pkill -9 iperf3 2>/dev/null || true");
            await Task.Delay(500);

            // Step 2: Start iperf3 server on the remote device
            _logger.LogDebug("Starting iperf3 server on {Host}", host);
            var serverStartResult = await _sshService.RunCommandAsync(host,
                $"nohup iperf3 -s -1 -p {Iperf3Port} > /tmp/iperf3_server.log 2>&1 & echo $!");

            if (!serverStartResult.success)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to start iperf3 server: {serverStartResult.output}";
                return result;
            }

            var serverPid = serverStartResult.output.Trim();
            _logger.LogDebug("iperf3 server started on {Host} with PID {Pid}", host, serverPid);

            // Give the server a moment to start
            await Task.Delay(1000);

            // Verify server is running by checking if iperf3 process exists (more reliable than ps -p on embedded devices)
            var checkResult = await _sshService.RunCommandAsync(host, "pgrep -x iperf3 > /dev/null 2>&1 && echo 'running' || echo 'stopped'");
            if (!checkResult.output.Contains("running"))
            {
                // Double-check with netstat/ss to see if port is listening
                var portCheck = await _sshService.RunCommandAsync(host, $"netstat -tln 2>/dev/null | grep -q ':{Iperf3Port}' && echo 'listening' || ss -tln 2>/dev/null | grep -q ':{Iperf3Port}' && echo 'listening' || echo 'not_listening'");
                if (!portCheck.output.Contains("listening"))
                {
                    var logResult = await _sshService.RunCommandAsync(host, "cat /tmp/iperf3_server.log 2>/dev/null");
                    result.Success = false;
                    result.ErrorMessage = $"iperf3 server failed to start. Log: {logResult.output}";
                    return result;
                }
                _logger.LogDebug("iperf3 process not found by pgrep but port {Port} is listening on {Host}", Iperf3Port, host);
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

                // Server runs with -1 (one-off mode), so restart for download test
                await _sshService.RunCommandAsync(host, "pkill -9 iperf3 2>/dev/null || true");
                await Task.Delay(500);

                // Restart server for download test
                await _sshService.RunCommandAsync(host,
                    $"nohup iperf3 -s -1 -p {Iperf3Port} > /tmp/iperf3_server.log 2>&1 &");
                await Task.Delay(1000);

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
                // Step 5: Always clean up - stop iperf3 server
                _logger.LogDebug("Stopping iperf3 server on {Host}", host);
                await _sshService.RunCommandAsync(host, "pkill -9 iperf3 2>/dev/null || true");
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

            // Try to clean up
            try
            {
                await _sshService.RunCommandAsync(host, "pkill -9 iperf3 2>/dev/null || true");
            }
            catch { }

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
        var args = $"-c {host} -p {Iperf3Port} -t {duration} -P {streams} -J";
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

            var timeoutMs = (duration + 30) * 1000;
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
