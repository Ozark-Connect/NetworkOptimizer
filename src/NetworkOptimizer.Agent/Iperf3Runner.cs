using System.ComponentModel;
using System.Diagnostics;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Keeps an iperf3 server (default port 5201) running alongside the speed
/// test page so site devices have a LAN throughput target. Uses the host's
/// iperf3 binary; if it isn't installed this logs once and gives up rather
/// than looping.
/// </summary>
public static class Iperf3Runner
{
    public static async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo("iperf3", "-s")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                process = Process.Start(psi);
                if (process == null)
                {
                    Console.Error.WriteLine("iperf3 could not be started - LAN iperf3 serving disabled");
                    return;
                }

                Console.WriteLine("iperf3 server running (default port 5201)");
                _ = DrainAsync(process.StandardOutput, ct);
                _ = DrainAsync(process.StandardError, ct);
                await process.WaitForExitAsync(ct);
                Console.Error.WriteLine($"iperf3 exited with code {process.ExitCode}, restarting in 10 seconds");
            }
            catch (OperationCanceledException)
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return;
            }
            catch (Win32Exception)
            {
                Console.Error.WriteLine("iperf3 binary not found on PATH - install iperf3 to serve LAN iperf3 tests");
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"iperf3 error: {ex.Message}");
            }
            finally
            {
                process?.Dispose();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) != null)
            {
            }
        }
        catch
        {
            // Process ended or cancelled - nothing to drain.
        }
    }
}
