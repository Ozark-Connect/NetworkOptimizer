using System.ComponentModel;
using System.Diagnostics;
using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Runs the iperf3 client at the site on the server's behalf. The central server
/// can't reach a secondary site's LAN devices directly, so it sends an
/// <see cref="Iperf3ClientRequest"/> over the tunnel; this runner executes
/// <c>iperf3 -c ...</c> locally against the site-local target and returns the raw
/// <c>-J</c> output. Uses the host's iperf3 binary (same one <see cref="Iperf3Runner"/>
/// serves with). The (success, output) contract mirrors the server's own local run.
/// </summary>
public sealed class Iperf3ClientRunner
{
    private readonly TunnelClient _tunnel;

    public Iperf3ClientRunner(TunnelClient tunnel)
    {
        _tunnel = tunnel;
    }

    /// <summary>Runs the requested client test and returns the result over the tunnel.</summary>
    public async Task HandleAsync(Iperf3ClientRequest request, CancellationToken ct)
    {
        var (success, output) = await RunAsync(request, ct);
        await _tunnel.SendAsync(new AgentMessage
        {
            Iperf3Result = new Iperf3ClientResult
            {
                RequestId = request.RequestId,
                Success = success,
                Output = output,
            }
        }, ct);
    }

    private static async Task<(bool success, string output)> RunAsync(Iperf3ClientRequest request, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("iperf3", Iperf3ClientArgs.Build(request))
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

            var timeoutMs = (request.DurationSeconds + 15) * 1000;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Client timed out - killed below.
            }

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "iperf3 client timed out");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
                return (false, string.IsNullOrEmpty(error) ? output : error);

            // iperf3 can exit 0 but carry an error in its JSON (e.g. connection
            // refused). Surface that as a failure, same as the server's local run.
            if (output.Contains("\"error\""))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    {
                        var errorMsg = errorProp.GetString();
                        if (!string.IsNullOrEmpty(errorMsg))
                            return (false, errorMsg);
                    }
                }
                catch
                {
                    // Unparseable - fall through and return the raw output.
                }
            }

            return (true, output);
        }
        catch (Win32Exception)
        {
            return (false, "iperf3 binary not found on PATH at the agent - install iperf3 to run LAN speed tests");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
