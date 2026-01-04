using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        _logger.LogInformation("Starting iperf3 server on port {Port}", Iperf3Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIperf3ServerAsync(stoppingToken);
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

    private async Task RunIperf3ServerAsync(CancellationToken stoppingToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "iperf3",
            Arguments = $"-s -p {Iperf3Port} -J", // Server mode, JSON output
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _iperf3Process = new Process { StartInfo = startInfo };

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

            // Extract client IP from connection info
            string? clientIp = null;
            if (root.TryGetProperty("start", out var start) &&
                start.TryGetProperty("connected", out var connected) &&
                connected.GetArrayLength() > 0)
            {
                var firstConn = connected[0];
                if (firstConn.TryGetProperty("remote_host", out var remoteHost))
                {
                    clientIp = remoteHost.GetString();
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

            // Parse end results - from server perspective:
            // sum_received = data we received from client = client UPLOAD
            // sum_sent = data we sent to client = client DOWNLOAD
            double uploadBps = 0;
            double downloadBps = 0;
            int? uploadRetransmits = null;
            int? downloadRetransmits = null;

            if (root.TryGetProperty("end", out var end))
            {
                // Client upload (server received)
                if (end.TryGetProperty("sum_received", out var sumReceived))
                {
                    uploadBps = sumReceived.GetProperty("bits_per_second").GetDouble();
                }

                // Client download (server sent)
                if (end.TryGetProperty("sum_sent", out var sumSent))
                {
                    downloadBps = sumSent.GetProperty("bits_per_second").GetDouble();
                    if (sumSent.TryGetProperty("retransmits", out var rt))
                        downloadRetransmits = rt.GetInt32();
                }
            }

            // Only record if we got meaningful data
            if (uploadBps > 0 || downloadBps > 0)
            {
                await _clientSpeedTestService.RecordIperf3ClientResultAsync(
                    clientIp,
                    downloadBps,
                    uploadBps,
                    downloadRetransmits,
                    uploadRetransmits,
                    durationSeconds,
                    parallelStreams,
                    json);

                _logger.LogInformation(
                    "Recorded iperf3 client test from {ClientIp}: Down {Download:F1} Mbps, Up {Upload:F1} Mbps",
                    clientIp, downloadBps / 1_000_000, uploadBps / 1_000_000);
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
}
