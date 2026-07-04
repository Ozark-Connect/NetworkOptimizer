using System.ComponentModel;
using System.Diagnostics;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Runs the UWN WAN speed test at the site on the server's behalf. The central
/// server's own WAN is the wrong link to measure for a secondary site, so it sends
/// a <see cref="UwnRequest"/> over the tunnel; this runner executes the site's
/// uwnspeedtest binary locally and returns the raw JSON it prints on stdout. The
/// server parses that JSON and stores the result, mirroring the (success, output)
/// contract of its own local run.
/// </summary>
public sealed class UwnClientRunner
{
    private readonly TunnelClient _tunnel;

    public UwnClientRunner(TunnelClient tunnel)
    {
        _tunnel = tunnel;
    }

    /// <summary>Runs the requested WAN speed test and returns the result over the tunnel.</summary>
    public async Task HandleAsync(UwnRequest request, CancellationToken ct)
    {
        var (success, output) = await RunAsync(request, ct);
        await _tunnel.SendAsync(new AgentMessage
        {
            UwnResult = new UwnResult
            {
                RequestId = request.RequestId,
                Success = success,
                Output = output,
            }
        }, ct);
    }

    /// <summary>
    /// The uwnspeedtest binary shipped alongside the agent. Defaults to
    /// <c>uwnspeedtest</c> next to the agent executable; override with
    /// <c>NO_AGENT_UWN_BINARY</c> when it lives elsewhere.
    /// </summary>
    private static string GetBinaryPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("NO_AGENT_UWN_BINARY");
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;
        return Path.Combine(AppContext.BaseDirectory, "uwnspeedtest");
    }

    private static async Task<(bool success, string output)> RunAsync(UwnRequest request, CancellationToken ct)
    {
        var binaryPath = GetBinaryPath();
        if (!File.Exists(binaryPath))
            return (false, $"uwnspeedtest binary not found at {binaryPath} on the agent - install it to run WAN speed tests at the site");

        var psi = new ProcessStartInfo(binaryPath, UwnClientArgs.Build(request))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            // The binary self-limits with -timeout; give it that plus headroom.
            var timeoutMs = (request.TimeoutSeconds + 30) * 1000;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Binary timed out - killed below.
            }

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "uwnspeedtest timed out");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
                return (false, string.IsNullOrEmpty(error) ? output : error);

            if (string.IsNullOrWhiteSpace(output))
                return (false, $"uwnspeedtest produced no output (exit code {process.ExitCode})");

            return (true, output);
        }
        catch (Win32Exception)
        {
            return (false, $"uwnspeedtest binary at {binaryPath} could not be executed on the agent");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
