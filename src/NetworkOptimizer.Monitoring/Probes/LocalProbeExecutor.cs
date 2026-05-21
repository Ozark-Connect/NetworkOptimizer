using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Runs probes from the Network Optimizer server itself.
///
/// Cross-platform notes:
/// - ICMP ping uses <see cref="Ping"/> from System.Net.NetworkInformation. Works on Linux
///   (Docker host with appropriate caps), macOS, and Windows.
/// - TCP probe is .NET sockets — fully cross-platform.
/// - Traceroute shells out to the OS binary. On Linux Docker we install the `traceroute`
///   package (see open question 9). On Windows we shell out to `tracert.exe`. On macOS
///   `traceroute` is built in.
///
/// Capability detection caches its result after the first call.
/// </summary>
public class LocalProbeExecutor : IProbeExecutor
{
    private readonly ILogger<LocalProbeExecutor> _logger;
    private readonly ManagedTraceroute _managedTraceroute;
    private ProbeCapability? _capability;
    private readonly SemaphoreSlim _capabilityLock = new(1, 1);
    private bool _tracerouteBinaryAvailable;

    public LocalProbeExecutor(ILogger<LocalProbeExecutor> logger)
    {
        _logger = logger;
        _managedTraceroute = new ManagedTraceroute(logger);
    }

    public ProbeVantage Vantage => ProbeVantage.Server;

    public async Task<ProbeCapability> GetCapabilityAsync(CancellationToken ct = default)
    {
        if (_capability != null) return _capability;
        await _capabilityLock.WaitAsync(ct);
        try
        {
            if (_capability != null) return _capability;

            // On Linux/macOS, prefer the native `traceroute` binary because it supports UDP
            // and TCP modes. The managed Ping-with-TTL traceroute is always available as a
            // fallback and is the primary path on Windows (where `tracert.exe` has a
            // different output format that would need its own parser).
            _tracerouteBinaryAvailable = !OperatingSystem.IsWindows() && await IsTracerouteInstalledAsync(ct);

            _capability = new ProbeCapability
            {
                CanIcmpPing = true,                          // System.Net.NetworkInformation.Ping
                CanIcmpTraceroute = true,                    // managed Ping-with-TTL always works
                CanUdpTraceroute = _tracerouteBinaryAvailable, // only the native binary does UDP
                CanTcpProbe = true,                          // .NET sockets
                IsBusyBoxPing = false,
                IsBusyBoxTraceroute = false
            };

            _logger.LogInformation(
                "Local probe capabilities: {Caps} (traceroute binary: {Binary})",
                _capability.Describe(),
                _tracerouteBinaryAvailable ? "available" : "absent (using managed fallback)");
            return _capability;
        }
        finally
        {
            _capabilityLock.Release();
        }
    }

    private async Task<bool> IsTracerouteInstalledAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("traceroute", "-V")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var probe = Process.Start(psi);
            if (probe == null) return false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try { await probe.WaitForExitAsync(cts.Token); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local traceroute binary probe failed");
            return false;
        }
    }

    public async Task<PingProbeResult> PingAsync(
        ProbeTarget target,
        int count = 10,
        TimeSpan? perPingTimeout = null,
        CancellationToken ct = default)
    {
        if (target.Mode == ProbeMode.Tcp)
        {
            // TCP "ping" reduces to repeated connects; report aggregate as ping result.
            return await TcpPingAsync(target, count, perPingTimeout ?? TimeSpan.FromSeconds(2), ct);
        }

        var timeout = (int)(perPingTimeout?.TotalMilliseconds ?? 2000);
        var rtts = new List<double>();
        int received = 0;
        string? lastError = null;

        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var reply = await ping.SendPingAsync(target.Address, timeout);
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    rtts.Add(reply.RoundtripTime);
                }
                else
                {
                    lastError = reply.Status.ToString();
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
            if (i < count - 1)
                await Task.Delay(200, ct);
        }

        double? min = rtts.Count > 0 ? rtts.Min() : null;
        double? avg = rtts.Count > 0 ? rtts.Average() : null;
        double? max = rtts.Count > 0 ? rtts.Max() : null;
        double? jitter = null;
        if (rtts.Count > 1)
        {
            var mean = rtts.Average();
            var variance = rtts.Sum(r => (r - mean) * (r - mean)) / rtts.Count;
            jitter = Math.Sqrt(variance);
        }

        return new PingProbeResult
        {
            Target = target,
            Vantage = Vantage,
            Sent = count,
            Received = received,
            RttMinMs = min,
            RttAvgMs = avg,
            RttMaxMs = max,
            JitterMs = jitter,
            ErrorMessage = received == 0 ? lastError : null,
            Timestamp = DateTime.UtcNow
        };
    }

    public async Task<TcpProbeResult> TcpProbeAsync(
        ProbeTarget target,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var port = target.Port ?? 443;
        var deadline = timeout ?? TimeSpan.FromSeconds(2);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(target.Address, port, cts.Token);
            sw.Stop();
            return new TcpProbeResult
            {
                Target = target,
                Vantage = Vantage,
                Connected = true,
                ConnectTimeMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            return new TcpProbeResult
            {
                Target = target,
                Vantage = Vantage,
                Connected = false,
                ErrorMessage = "timeout",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new TcpProbeResult
            {
                Target = target,
                Vantage = Vantage,
                Connected = false,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    public async Task<TracerouteResult> TracerouteAsync(
        ProbeTarget target,
        int maxHops = 30,
        TimeSpan? perHopTimeout = null,
        TimeSpan? totalDeadline = null,
        CancellationToken ct = default)
    {
        await GetCapabilityAsync(ct);

        // Cap the whole probe by an absolute deadline — some hops silently discard probes
        // and a per-hop * 30 hops worst case can stretch past a minute, which is too long
        // for a UI button or scheduled probe.
        var deadline = totalDeadline ?? TimeSpan.FromSeconds(10);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(deadline);
        var probeCt = deadlineCts.Token;

        // Prefer the native traceroute binary on Linux/macOS for every mode (including
        // ICMP — `traceroute -I`). The binary ships setuid / with proper capabilities so
        // it doesn't need CAP_NET_RAW on the calling process, and it captures PTR records
        // that the managed implementation can't get. On Windows MSI installs the binary
        // isn't available and tracert.exe's output is a different format, so we fall back
        // to the managed Ping-with-TTL implementation only there (or as a last-resort
        // fallback when traceroute is genuinely missing).
        if (!_tracerouteBinaryAvailable || OperatingSystem.IsWindows())
        {
            return await _managedTraceroute.RunAsync(target, Vantage, maxHops, perHopTimeout, 3, probeCt);
        }

        var (exe, args) = BuildTracerouteCommand(target, maxHops, perHopTimeout);
        ct = probeCt;
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return new TracerouteResult
                {
                    Target = target,
                    Vantage = Vantage,
                    ModeUsed = target.Mode,
                    Hops = Array.Empty<TraceHop>(),
                    Reached = false,
                    ErrorMessage = "Failed to start traceroute",
                    Timestamp = DateTime.UtcNow
                };
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Deadline hit — kill the binary so it doesn't keep running, then parse
                // whatever output we got so far. A partial traceroute is more useful than
                // a hard error.
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : await SafeReadAsync(stdoutTask);
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : await SafeReadAsync(stderrTask);

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return TracerouteOutputParser.Parse(output, target, Vantage, target.Mode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Traceroute to {Target} failed", target);
            return new TracerouteResult
            {
                Target = target,
                Vantage = Vantage,
                ModeUsed = target.Mode,
                Hops = Array.Empty<TraceHop>(),
                Reached = false,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try { return await readTask; }
        catch { return string.Empty; }
    }

    private async Task<PingProbeResult> TcpPingAsync(ProbeTarget target, int count, TimeSpan timeout, CancellationToken ct)
    {
        var rtts = new List<double>();
        int received = 0;
        string? lastError = null;

        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var r = await TcpProbeAsync(target, timeout, ct);
            if (r.Connected && r.ConnectTimeMs.HasValue)
            {
                received++;
                rtts.Add(r.ConnectTimeMs.Value);
            }
            else
            {
                lastError = r.ErrorMessage;
            }
            if (i < count - 1) await Task.Delay(200, ct);
        }

        return new PingProbeResult
        {
            Target = target,
            Vantage = Vantage,
            Sent = count,
            Received = received,
            RttMinMs = rtts.Count > 0 ? rtts.Min() : null,
            RttAvgMs = rtts.Count > 0 ? rtts.Average() : null,
            RttMaxMs = rtts.Count > 0 ? rtts.Max() : null,
            JitterMs = rtts.Count > 1 ? StdDev(rtts) : null,
            ErrorMessage = received == 0 ? lastError : null,
            Timestamp = DateTime.UtcNow
        };
    }

    private static double StdDev(IReadOnlyCollection<double> v)
    {
        var mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
    }

    private static (string exe, string args) ChooseTracerouteBinary()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("tracert.exe", "-h 1 127.0.0.1");
        }
        return ("traceroute", "-V");
    }

    private static (string exe, string args) BuildTracerouteCommand(ProbeTarget target, int maxHops, TimeSpan? perHopTimeout)
    {
        var wait = (int)Math.Max(1, (perHopTimeout ?? TimeSpan.FromSeconds(2)).TotalSeconds);
        if (OperatingSystem.IsWindows())
        {
            // tracert: -h max hops, -w wait ms, -d no DNS resolution to speed up
            return ("tracert.exe", $"-h {maxHops} -w {wait * 1000} {target.Address}");
        }

        var protoFlag = target.Mode switch
        {
            ProbeMode.Icmp => "-I",
            ProbeMode.Tcp => $"-T -p {target.Port ?? 80}",
            _ => string.Empty // default UDP
        };
        // PTR resolution stays ON — hostnames like "cr1.stl1.yelcot.net" are gold for the
        // wizard's hop-labelling logic (spec 5.5). Linux's resolver times out fast, so the
        // cost is bounded by the per-probe deadline anyway.
        var args = $"-m {maxHops} -w {wait} {protoFlag} {target.Address}".Trim();
        return ("traceroute", args);
    }
}
