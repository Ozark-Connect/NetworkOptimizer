using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Background service that runs iperf3 in server mode and monitors for client-initiated tests.
/// Parses JSON output and records results via ClientSpeedTestService.
/// </summary>
public class Iperf3ServerService : BackgroundService
{
    private readonly ILogger<Iperf3ServerService> _logger;
    private readonly ClientSpeedTestService _clientSpeedTestService;
    private readonly IConfiguration _configuration;

    private Process? _iperf3Process;
    private const int Iperf3Port = 5201;

    public Iperf3ServerService(
        ILogger<Iperf3ServerService> logger,
        ClientSpeedTestService clientSpeedTestService,
        IConfiguration configuration)
    {
        _logger = logger;
        _clientSpeedTestService = clientSpeedTestService;
        _configuration = configuration;
    }

    /// <summary>
    /// Whether the iperf3 server is currently running
    /// </summary>
    public bool IsRunning => _iperf3Process is { HasExited: false };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if iperf3 server mode is enabled
        var enabled = _configuration.GetValue("Iperf3Server:Enabled", false);
        if (!enabled)
        {
            _logger.LogInformation("iperf3 server mode is disabled. Enable via Iperf3Server:Enabled=true");
            return;
        }

        // Kill any orphaned iperf3 server processes from previous runs
        // This is especially important for native deployments where launchd/systemd
        // may not kill child processes when stopping the app
        await KillOrphanedIperf3ProcessesAsync();

        _logger.LogInformation("Starting iperf3 server on port {Port}", Iperf3Port);

        var consecutiveImmediateExits = 0;
        const int maxImmediateExitRetries = 5;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ranSuccessfully = await RunIperf3ServerAsync(stoppingToken);

                if (ranSuccessfully)
                {
                    consecutiveImmediateExits = 0;
                }
                else
                {
                    consecutiveImmediateExits++;

                    if (consecutiveImmediateExits >= maxImmediateExitRetries)
                    {
                        _logger.LogError(
                            "iperf3 server failed to start {Count} consecutive times, giving up. Check if port {Port} is in use.",
                            consecutiveImmediateExits, Iperf3Port);
                        break;
                    }

                    // Exponential backoff: 1s, 2s, 4s, 8s, 16s
                    var delaySeconds = (int)Math.Pow(2, consecutiveImmediateExits - 1);
                    _logger.LogWarning(
                        "Waiting {Delay}s before retry (attempt {Attempt}/{Max})",
                        delaySeconds, consecutiveImmediateExits, maxImmediateExitRetries);
                    await Task.Delay(delaySeconds * 1000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "iperf3 server crashed, restarting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }

        _logger.LogInformation("iperf3 server stopped");
    }

    /// <summary>
    /// Runs the iperf3 server process until it exits or cancellation is requested.
    /// </summary>
    /// <returns>True if the process ran for more than 2 seconds (successful), false if it exited immediately.</returns>
    private async Task<bool> RunIperf3ServerAsync(CancellationToken stoppingToken)
    {
        var iperf3Path = GetIperf3Path();
        _logger.LogDebug("Using iperf3 at: {Path}", iperf3Path);

        var startInfo = new ProcessStartInfo
        {
            FileName = iperf3Path,
            Arguments = $"-s -p {Iperf3Port} -J", // Server mode, JSON output
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _iperf3Process = new Process { StartInfo = startInfo };
        var startTime = DateTime.UtcNow;

        // Buffer to accumulate JSON
        var jsonBuffer = new StringBuilder();
        var braceCount = 0;
        var inJson = false;

        _iperf3Process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data == null) return;

            var line = e.Data;

            // Track JSON object boundaries
            foreach (var ch in line)
            {
                if (ch == '{')
                {
                    if (!inJson)
                    {
                        inJson = true;
                        jsonBuffer.Clear();
                    }
                    braceCount++;
                }

                if (inJson)
                {
                    jsonBuffer.Append(ch);
                }

                if (ch == '}' && inJson)
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        // Complete JSON object received
                        var json = jsonBuffer.ToString();
                        jsonBuffer.Clear();
                        inJson = false;

                        // Process asynchronously
                        _ = ProcessCompletedTestAsync(json);
                    }
                }
            }

            if (inJson)
            {
                jsonBuffer.AppendLine();
            }
        };

        _iperf3Process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogWarning("iperf3 server stderr: {Message}", e.Data);
            }
        };

        _iperf3Process.Start();
        _iperf3Process.BeginOutputReadLine();
        _iperf3Process.BeginErrorReadLine();

        _logger.LogInformation("iperf3 server started with PID {Pid}", _iperf3Process.Id);

        // Wait for process to exit or cancellation
        try
        {
            await _iperf3Process.WaitForExitAsync(stoppingToken);

            var runtime = DateTime.UtcNow - startTime;
            var exitCode = _iperf3Process.ExitCode;
            var ranSuccessfully = runtime.TotalSeconds >= 2;

            if (!ranSuccessfully)
            {
                _logger.LogWarning(
                    "iperf3 server exited immediately (exit code {ExitCode}, ran for {Runtime:F1}s) - port {Port} may already be in use",
                    exitCode, runtime.TotalSeconds, Iperf3Port);
            }
            else
            {
                _logger.LogInformation(
                    "iperf3 server exited (exit code {ExitCode}, ran for {Runtime:F1}s), restarting",
                    exitCode, runtime.TotalSeconds);
            }

            return ranSuccessfully;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Stopping iperf3 server process");
            try
            {
                _iperf3Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error killing iperf3 process");
            }
            throw;
        }
        finally
        {
            _iperf3Process.Dispose();
            _iperf3Process = null;
        }
    }

    private async Task ProcessCompletedTestAsync(string json)
    {
        try
        {
            _logger.LogDebug("Processing iperf3 server test result");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for errors
            if (root.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString();
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    _logger.LogDebug("iperf3 test error: {Error}", errorMsg);
                    return;
                }
            }

            // Extract client IP and server's local IP from connection info
            string? clientIp = null;
            string? serverLocalIp = null;
            if (root.TryGetProperty("start", out var start) &&
                start.TryGetProperty("connected", out var connected) &&
                connected.GetArrayLength() > 0)
            {
                var firstConn = connected[0];
                if (firstConn.TryGetProperty("remote_host", out var remoteHost))
                {
                    clientIp = remoteHost.GetString();
                }
                if (firstConn.TryGetProperty("local_host", out var localHost))
                {
                    serverLocalIp = localHost.GetString();
                }
            }

            if (string.IsNullOrEmpty(clientIp))
            {
                _logger.LogWarning("Could not extract client IP from iperf3 result");
                return;
            }

            // Extract test parameters
            int durationSeconds = 10;
            int parallelStreams = 1;
            if (root.TryGetProperty("start", out var startInfo) &&
                startInfo.TryGetProperty("test_start", out var testStart))
            {
                if (testStart.TryGetProperty("duration", out var dur))
                    durationSeconds = dur.GetInt32();
                if (testStart.TryGetProperty("num_streams", out var streams))
                    parallelStreams = streams.GetInt32();
            }

            // Parse end results - from SERVER perspective:
            // sum_received = data server received FROM client = "From Device" = DownloadBitsPerSecond
            // sum_sent = data server sent TO client = "To Device" = UploadBitsPerSecond
            //
            // For bidir tests (--bidir), iperf3 also outputs:
            // sum_sent_bidir_reverse / sum_received_bidir_reverse for the reverse direction
            double fromDeviceBps = 0;  // Server download = received from client
            double toDeviceBps = 0;    // Server upload = sent to client
            long fromDeviceBytes = 0;
            long toDeviceBytes = 0;
            int? fromDeviceRetransmits = null;
            int? toDeviceRetransmits = null;

            if (root.TryGetProperty("end", out var end))
            {
                // Data received FROM client (server download = "From Device")
                // Check both regular and bidir_reverse variants
                if (end.TryGetProperty("sum_received", out var sumReceived))
                {
                    fromDeviceBps = sumReceived.GetProperty("bits_per_second").GetDouble();
                    if (sumReceived.TryGetProperty("bytes", out var bytes))
                        fromDeviceBytes = bytes.GetInt64();
                    if (sumReceived.TryGetProperty("retransmits", out var rt))
                        fromDeviceRetransmits = rt.GetInt32();
                }

                // Data sent TO client (server upload = "To Device")
                if (end.TryGetProperty("sum_sent", out var sumSent))
                {
                    toDeviceBps = sumSent.GetProperty("bits_per_second").GetDouble();
                    if (sumSent.TryGetProperty("bytes", out var bytes))
                        toDeviceBytes = bytes.GetInt64();
                    if (sumSent.TryGetProperty("retransmits", out var rt))
                        toDeviceRetransmits = rt.GetInt32();
                }

                // For bidir tests: check _bidir_reverse variants
                // In bidir mode, there are two simultaneous tests:
                //   Normal: client → server (sum_received has client upload data)
                //   Reverse: server → client (sum_sent_bidir_reverse has client download data)
                //
                // sum_sent_bidir_reverse = server sent TO client in reverse channel = "To Device"
                if (end.TryGetProperty("sum_sent_bidir_reverse", out var sumSentReverse))
                {
                    var reverseBps = sumSentReverse.GetProperty("bits_per_second").GetDouble();
                    // This is the actual download speed in bidir mode
                    if (reverseBps > 0)
                    {
                        toDeviceBps = reverseBps;
                        if (sumSentReverse.TryGetProperty("bytes", out var bytes))
                            toDeviceBytes = bytes.GetInt64();
                        if (sumSentReverse.TryGetProperty("retransmits", out var rt))
                            toDeviceRetransmits = rt.GetInt32();
                    }
                }
            }

            // Only record if we got meaningful data
            if (fromDeviceBps > 0 || toDeviceBps > 0)
            {
                await _clientSpeedTestService.RecordIperf3ClientResultAsync(
                    clientIp,
                    fromDeviceBps,   // DownloadBitsPerSecond = From Device
                    toDeviceBps,     // UploadBitsPerSecond = To Device
                    fromDeviceBytes, // DownloadBytes = From Device
                    toDeviceBytes,   // UploadBytes = To Device
                    fromDeviceRetransmits,
                    toDeviceRetransmits,
                    durationSeconds,
                    parallelStreams,
                    json,
                    serverLocalIp);  // Actual server interface IP from iperf3

                _logger.LogInformation(
                    "Recorded iperf3 client test from {ClientIp}: From Device {FromDevice:F1} Mbps, To Device {ToDevice:F1} Mbps",
                    clientIp, fromDeviceBps / 1_000_000, toDeviceBps / 1_000_000);
            }
            else
            {
                _logger.LogDebug("iperf3 test from {ClientIp} had no measurable data", clientIp);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse iperf3 server JSON output");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing iperf3 server test result");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping iperf3 server service");

        if (_iperf3Process is { HasExited: false })
        {
            try
            {
                _iperf3Process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error killing iperf3 process on stop");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Kill any orphaned iperf3 server processes that may be left over from a previous run.
    /// This handles the case where the app was stopped but child processes weren't killed
    /// (common with launchd on macOS).
    /// </summary>
    private async Task KillOrphanedIperf3ProcessesAsync()
    {
        try
        {
            // Use pkill on Unix-like systems (macOS, Linux)
            if (!OperatingSystem.IsWindows())
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "pkill",
                    // Use -9 (SIGKILL) to ensure process dies, simple pattern matching
                    Arguments = "-9 iperf3",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("Killed orphaned iperf3 server process(es)");
                        // Brief delay to ensure port is released
                        await Task.Delay(500);
                    }
                    // Exit code 1 means no matching processes found, which is fine
                }
            }
            else
            {
                // On Windows, find and kill iperf3.exe processes in server mode
                // We check the command line for "-s" to avoid killing client instances
                foreach (var proc in Process.GetProcessesByName("iperf3"))
                {
                    try
                    {
                        proc.Kill();
                        _logger.LogInformation("Killed orphaned iperf3 server process (PID {Pid})", proc.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not kill iperf3 process {Pid}", proc.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking for orphaned iperf3 processes");
        }
    }

    /// <summary>
    /// Gets the path to the iperf3 executable.
    /// On Windows, looks for bundled iperf3 in the install directory.
    /// On Linux/macOS, uses iperf3 from PATH.
    /// </summary>
    private static string GetIperf3Path()
    {
        if (OperatingSystem.IsWindows())
        {
            // Look for bundled iperf3 relative to the application directory
            // This works regardless of where the app is installed
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "iperf3", "iperf3.exe");

            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }
        }

        // Fall back to iperf3 in PATH (Linux/macOS/Docker)
        return "iperf3";
    }
}
