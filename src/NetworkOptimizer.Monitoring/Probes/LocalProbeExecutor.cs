using System.Diagnostics;
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

    // Throttle native Process.Start. macOS ARM64 has a .NET 10 runtime bug
    // (dotnet/runtime#112167) where concurrent Process.Start with redirected
    // stdout/stderr causes heap corruption and Abort trap: 6. Limit to 2 on
    // macOS (faster 0.1s ping interval compensates). Linux is unaffected.
    private readonly SemaphoreSlim _processLaunchLimiter = new(
        OperatingSystem.IsMacOS() ? 2 : 4,
        OperatingSystem.IsMacOS() ? 2 : 4);

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

        // On Linux and macOS, shell out to the native `ping` binary - same approach STM
        // takes. Kernel-timestamped RTTs are sub-ms accurate; userspace overhead from
        // .NET's Ping class adds 1-2 ms which makes LAN measurements visibly wrong
        // ("ping" says 0.2 ms, dashboard says 1.5 ms). Windows ping has different output
        // and gives less useful data, so the managed Ping + Stopwatch path is the
        // Windows MSI fallback.
        if (!OperatingSystem.IsWindows())
        {
            return await NativePingAsync(target, count, perPingTimeout ?? TimeSpan.FromSeconds(2), ct);
        }

        return await ManagedPingAsync(target, count, perPingTimeout ?? TimeSpan.FromSeconds(2), ct);
    }

    private async Task<PingProbeResult> NativePingAsync(ProbeTarget target, int count, TimeSpan timeout, CancellationToken ct)
    {
        // Fixed interval per platform: 0.1s on macOS (BSD ping allows it),
        // 0.2s on Linux (iputils enforces 200ms minimum for non-root).
        var safeCount = Math.Max(1, count);
        var interval = OperatingSystem.IsMacOS() ? 0.1 : 0.2;
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));

        // Two BSD-vs-iputils gotchas: ping has no -4 on macOS (it aborts with
        // "invalid option"; IPv4 is the default for an IPv4 literal anyway),
        // and -W is in SECONDS on Linux iputils but MILLISECONDS on BSD ping.
        var waitArg = OperatingSystem.IsMacOS()
            ? $"-W {timeoutSeconds * 1000}"
            : $"-W {timeoutSeconds}";

        // Source binding for multi-WAN probing: iputils -I takes an address or
        // interface name; BSD ping wants -S for a source address and -b for an
        // interface. The gateway PBRs the bound source out a specific WAN.
        var sourceArg = "";
        if (!string.IsNullOrEmpty(target.SourceInterface))
        {
            if (!IsSafeSourceValue(target.SourceInterface))
                return Fail(target, safeCount, $"Invalid probe source '{target.SourceInterface}'");
            sourceArg = OperatingSystem.IsMacOS()
                ? System.Net.IPAddress.TryParse(target.SourceInterface, out _)
                    ? $"-S {target.SourceInterface} "
                    : $"-b {target.SourceInterface} "
                : $"-I {target.SourceInterface} ";
        }

        var psi = new ProcessStartInfo("ping",
            $"-c {safeCount} -i {interval.ToString(System.Globalization.CultureInfo.InvariantCulture)} {waitArg} {sourceArg}{target.Address}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await _processLaunchLimiter.WaitAsync(ct);
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                // System ping somehow unavailable - degrade to managed.
                return await ManagedPingAsync(target, count, timeout, ct);
            }
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            // Cap the wall-clock overall in case ping hangs (interval * count + timeout, plus a small margin).
            var overall = TimeSpan.FromSeconds(interval * safeCount + timeoutSeconds + 5);
            using var killCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            killCts.CancelAfter(overall);
            try { await proc.WaitForExitAsync(killCts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
            var stdout = await SafeReadAsync(stdoutTask);

            var parsed = PingOutputParser.Parse(stdout, target, Vantage, safeCount);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Native ping invocation failed; falling back to managed Ping");
            return await ManagedPingAsync(target, count, timeout, ct);
        }
        finally
        {
            _processLaunchLimiter.Release();
        }
    }

    private async Task<PingProbeResult> ManagedPingAsync(ProbeTarget target, int count, TimeSpan timeout, CancellationToken ct)
    {
        // .NET's Ping class cannot bind a source address; silently probing out
        // the default route would attribute the wrong WAN's latency to this
        // target, so fail loudly instead.
        if (!string.IsNullOrEmpty(target.SourceInterface))
            return Fail(target, count, "Source-bound probes need the native ping binary (Linux/macOS)");

        var timeoutMs = (int)timeout.TotalMilliseconds;
        var rtts = new List<double>();
        int received = 0;
        string? lastError = null;

        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // PingReply.RoundtripTime is integer ms which floors sub-ms LAN pings to 0.
                // Stopwatch captures wall-clock around the call, which includes ~1-2 ms of
                // .NET userspace overhead vs the kernel's actual ICMP RTT. Acceptable on
                // Windows because the alternative (parsing tracert.exe / Windows ping
                // output) is messier; Linux/macOS use the native binary above for accurate
                // numbers.
                var sw = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(target.Address, timeoutMs);
                sw.Stop();
                if (reply.Status == IPStatus.Success)
                {
                    received++;
                    rtts.Add(sw.Elapsed.TotalMilliseconds);
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
            if (!string.IsNullOrEmpty(target.SourceInterface))
            {
                // TCP source binding only works with an address (SO_BINDTODEVICE
                // for interface names needs CAP_NET_RAW; not worth it here).
                if (!System.Net.IPAddress.TryParse(target.SourceInterface, out var sourceIp))
                {
                    return new TcpProbeResult
                    {
                        Target = target,
                        Vantage = Vantage,
                        Connected = false,
                        ErrorMessage = $"TCP probes need an IP address as the probe source, got '{target.SourceInterface}'",
                        Timestamp = DateTime.UtcNow
                    };
                }
                tcp.Client.Bind(new System.Net.IPEndPoint(sourceIp, 0));
            }
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

        // Prefer the native traceroute binary on Linux/macOS for every mode (including
        // ICMP — `traceroute -I`). The binary ships setuid / with proper capabilities so
        // it doesn't need CAP_NET_RAW on the calling process, and it captures PTR records
        // that the managed implementation can't get. On Windows MSI installs the binary
        // isn't available and tracert.exe's output is a different format, so we fall back
        // to the managed Ping-with-TTL implementation only there (or as a last-resort
        // fallback when traceroute is genuinely missing).
        var deadlineDuration = totalDeadline ?? TimeSpan.FromSeconds(10);
        if (!_tracerouteBinaryAvailable || OperatingSystem.IsWindows())
        {
            using var managedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            managedCts.CancelAfter(deadlineDuration);
            return await _managedTraceroute.RunAsync(target, Vantage, maxHops, perHopTimeout, 3, managedCts.Token);
        }

        var (exe, args) = BuildTracerouteCommand(target, maxHops, perHopTimeout);
        // Acquire the throttle FIRST, THEN start the per-trace deadline. The
        // deadline must bound process execution, not time spent queued behind
        // the semaphore - otherwise queued traces in a big sweep (18 in the
        // wizard) burn their entire budget waiting and exit with no output.
        await _processLaunchLimiter.WaitAsync(ct);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(deadlineDuration);
        var probeCt = deadlineCts.Token;
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
        finally
        {
            _processLaunchLimiter.Release();
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

    /// <summary>
    /// The probe source goes into a process argument, so restrict it to the
    /// characters valid in IPv4/IPv6 addresses and interface names.
    /// </summary>
    private static bool IsSafeSourceValue(string value) =>
        value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '-' or '_' or '%');

    private PingProbeResult Fail(ProbeTarget target, int sent, string error) => new()
    {
        Target = target,
        Vantage = Vantage,
        Sent = sent,
        Received = 0,
        ErrorMessage = error,
        Timestamp = DateTime.UtcNow
    };

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
        // PTR resolution stays ON — hostnames like "cr1.stl1.example.net" are gold for the
        // wizard's hop-labelling logic (spec 5.5). Linux's resolver times out fast, so the
        // cost is bounded by the per-probe deadline anyway.
        var args = $"-m {maxHops} -q 2 -w {wait} {protoFlag} {target.Address}".Trim();
        return ("traceroute", args);
    }
}
