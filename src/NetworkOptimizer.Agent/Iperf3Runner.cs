using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Keeps an iperf3 server (default port 5201, JSON output) running alongside the speed test page so
/// site devices have a LAN throughput target. Each completed client-initiated test's <c>-J</c> JSON
/// is captured (brace-counted off stdout, mirroring the central server's managed iperf3 server) and
/// relayed to the central server via <c>relayResult</c>, so client-initiated iperf3 results land in
/// the site's database exactly like the default site's do. Uses the host's iperf3 binary; if it
/// isn't installed this logs once and gives up rather than looping.
/// </summary>
public static class Iperf3Runner
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    // A server that stayed up this long was working; the next failure starts a fresh backoff rather
    // than inheriting the delay from whatever went wrong before it.
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1);

    public static async Task RunAsync(Func<string, CancellationToken, Task>? relayResult, CancellationToken ct)
    {
        var failures = 0;
        string? lastReason = null;

        while (!ct.IsCancellationRequested)
        {
            Process? process = null;
            var startedAt = DateTime.UtcNow;
            try
            {
                // Shared server args (-s -p {port} -J) so the emitted per-test JSON matches what
                // CaptureResultsAsync (and the central Iperf3ServerService) brace-counts.
                var psi = new ProcessStartInfo("iperf3", Iperf3ServerArgs.Build())
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
                // iperf3 -J reports a refusal to start on STDOUT as {"error": "..."} rather than on
                // stderr, so the only account of why it would not start arrives through the same
                // stream as the results. Kept here so the exit can be explained with it.
                var stdoutError = new ErrorSink();
                var stdout = CaptureResultsAsync(process.StandardOutput, relayResult, stdoutError, ct);
                // Kept rather than drained: when iperf3 refuses to start, its reason is the only
                // thing that distinguishes "someone else already has 5201" from a real fault, and
                // discarding it left an exit code and nothing to act on.
                var stderr = CaptureLastLineAsync(process.StandardError, ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode == 0)
                {
                    // A clean exit is a stop we did not ask for (a signal, usually). Start again,
                    // but not instantly - an immediate loop here would spin.
                    failures = 0;
                    lastReason = null;
                    await Task.Delay(FirstRetry, ct);
                    continue;
                }

                await stdout;
                var detail = await stderr;
                var reason = FirstNonEmpty(stdoutError.Message, detail) ?? $"exit code {process.ExitCode}";

                if (DateTime.UtcNow - startedAt > HealthyRun)
                {
                    failures = 0;
                    lastReason = null;
                }
                failures++;

                var delay = BackoffFor(failures);
                // Say it once. This loop ran forever at a fixed ten seconds, so a port that was
                // never going to free up produced an identical pair of lines every ten seconds for
                // as long as the agent lived.
                if (reason != lastReason)
                {
                    lastReason = reason;
                    Console.Error.WriteLine(IsPortInUse(reason)
                        ? "iperf3 could not take port 5201 - something else on this host is already "
                          + $"serving it. LAN iperf3 tests will not run until that stops. ({reason})"
                        : $"iperf3 exited: {reason}");
                    Console.Error.WriteLine($"Retrying every {delay.TotalSeconds:0}s"
                        + (delay < MaxRetry ? $", backing off to {MaxRetry.TotalMinutes:0} minutes" : "")
                        + " until it starts.");
                }

                await Task.Delay(delay, ct);
                continue;
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

            try { await Task.Delay(BackoffFor(++failures), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Holds the last error iperf3 wrote to stdout, for explaining an exit.</summary>
    private sealed class ErrorSink
    {
        public string? Message;
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();

    /// <summary>The "error" text of an iperf3 JSON object, or null if it has none.</summary>
    private static string? ErrorTextOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan BackoffFor(int failures)
    {
        var seconds = FirstRetry.TotalSeconds * Math.Pow(2, Math.Max(0, failures - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetry.TotalSeconds));
    }

    private static bool IsPortInUse(string reason) =>
        reason.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("unable to start listener", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brace-counts <c>iperf3 -s -J</c> stdout to isolate each completed test's JSON object and
    /// relays it, mirroring the central <c>Iperf3ServerService</c>'s capture exactly.
    /// </summary>
    private static async Task CaptureResultsAsync(StreamReader reader, Func<string, CancellationToken, Task>? relayResult, ErrorSink errors, CancellationToken ct)
    {
        var accumulator = new JsonObjectAccumulator();
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
            {
                accumulator.Feed(line, json =>
                {
                    // A refusal to start is also a JSON object on stdout ({"error": "..."}), so the
                    // brace-counter cannot tell it from a finished test. Relaying it posted a
                    // non-result to the server on every restart, which answered 404 each time.
                    if (relayResult != null && IsTestResult(json))
                        _ = relayResult(json, ct);
                    else
                        errors.Message = ErrorTextOf(json) ?? errors.Message;
                });
            }
        }
        catch
        {
            // Process ended or cancelled - nothing to capture.
        }
    }

    /// <summary>A finished test carries an "end" section; iperf3's error objects carry "error".</summary>
    private static bool IsTestResult(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && !doc.RootElement.TryGetProperty("error", out _)
                && doc.RootElement.TryGetProperty("end", out _);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> CaptureLastLineAsync(StreamReader reader, CancellationToken ct)
    {
        string? last = null;
        try
        {
            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line)) last = line;
            }
        }
        catch
        {
            // Process ended or cancelled - whatever we have is what we report.
        }
        return last;
    }
}
