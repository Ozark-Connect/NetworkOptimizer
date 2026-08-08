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
    private TracerouteBinaryTraits _tracerouteTraits = TracerouteBinaryTraits.FullyBindable;

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

    /// <summary>
    /// Whether probes on this host can be bound to a source address or interface.
    /// Binding rides on the native ping binary's source options, so it is exactly
    /// the platforms where <see cref="NativePingAsync"/> is the ping path: .NET's
    /// managed Ping cannot bind at all. Agents announce this in their hello so the
    /// server only offers a bind mechanism the agent can actually honor.
    /// </summary>
    public static bool SupportsSourceBinding => !OperatingSystem.IsWindows();

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
                IsBusyBoxTraceroute = _tracerouteBinaryAvailable && _tracerouteTraits.IsBusyBox
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
            // Both streams: GNU traceroute prints its version on stdout, while BusyBox and
            // BSD answer an unknown -V with their usage on stderr. That usage text is the
            // only evidence available for which source-bind options this build actually has.
            // On the same 2s budget as the exit wait, so a binary that says nothing and never
            // returns costs the same as it did when nothing read its output at all.
            var stdoutTask = probe.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = probe.StandardError.ReadToEndAsync(cts.Token);
            try { await probe.WaitForExitAsync(cts.Token); } catch { }
            var banner = (await SafeReadAsync(stdoutTask) ?? string.Empty)
                + "\n" + (await SafeReadAsync(stderrTask) ?? string.Empty);
            _tracerouteTraits = InterpretTracerouteBanner(banner);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Local traceroute binary probe failed");
            return false;
        }
    }

    /// <summary>
    /// What the installed traceroute can be told about where a probe leaves from. GNU
    /// traceroute and BSD traceroute both take <c>-s</c> (source address) and <c>-i</c>
    /// (source interface), so anything that isn't BusyBox is taken as fully bindable.
    /// BusyBox's applet is compile-configurable and may carry neither, so its usage text -
    /// which it prints in place of a version - is read for the two options before either is
    /// offered. A probe that cannot bind must fail rather than leave by the default route:
    /// that would record another WAN's latency under this one's name.
    /// </summary>
    internal readonly record struct TracerouteBinaryTraits(bool IsBusyBox, bool CanBindAddress, bool CanBindInterface)
    {
        /// <summary>A GNU/BSD traceroute: both bind options present. Also the assumption before detection runs.</summary>
        public static TracerouteBinaryTraits FullyBindable => new(false, true, true);
    }

    /// <summary>Reads a traceroute binary's version/usage output into the bind options it advertises.</summary>
    /// <param name="banner">Combined stdout+stderr from <c>traceroute -V</c>; empty when it could not be read.</param>
    internal static TracerouteBinaryTraits InterpretTracerouteBanner(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return TracerouteBinaryTraits.FullyBindable;
        if (banner.IndexOf("busybox", StringComparison.OrdinalIgnoreCase) < 0)
            return TracerouteBinaryTraits.FullyBindable;

        return new TracerouteBinaryTraits(
            IsBusyBox: true,
            CanBindAddress: MentionsOption(banner, 's'),
            CanBindInterface: MentionsOption(banner, 'i'));
    }

    /// <summary>Whether a usage line lists a single-letter option, either bare or inside a bundled flag group.</summary>
    private static bool MentionsOption(string banner, char option)
    {
        for (var i = 0; i < banner.Length - 1; i++)
        {
            if (banner[i] != '-') continue;
            for (var j = i + 1; j < banner.Length && char.IsAsciiLetterOrDigit(banner[j]); j++)
                if (banner[j] == option) return true;
        }
        return false;
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
        if (SupportsSourceBinding)
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
            Observe(stderrTask);
            if (stdout is null)
                throw new ProbeOutputTimeoutException(
                    $"ping output read for {target.Address} did not complete within {OutputReadGrace.TotalSeconds:0}s");

            var parsed = PingOutputParser.Parse(stdout, target, Vantage, safeCount);
            return parsed;
        }
        // Output-timeout means async completion delivery is broken, and the managed
        // Ping path depends on the same machinery - falling back would hang or
        // fabricate. Let it propagate; callers drop the sample.
        catch (Exception ex) when (ex is not ProbeOutputTimeoutException)
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
        string? resolvedAddress = null;

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
                    // Only a successful reply carries a real peer; a failure reports 0.0.0.0.
                    resolvedAddress ??= reply.Address?.ToString();
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
            ResolvedAddress = string.Equals(resolvedAddress, target.Address, StringComparison.OrdinalIgnoreCase)
                ? null
                : resolvedAddress,
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
                // TCP source binding takes an address, not a device (SO_BINDTODEVICE
                // for interface names needs CAP_NET_RAW), so an interface name is
                // resolved to its current address here rather than at push time: a
                // DHCP or PPPoE WAN moves, and a stale address binds nothing.
                var (sourceIp, error) = ResolveTcpBindAddress(target.SourceInterface, LookupInterfaceIPv4);
                if (sourceIp == null)
                {
                    return new TcpProbeResult
                    {
                        Target = target,
                        Vantage = Vantage,
                        Connected = false,
                        ErrorMessage = error,
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
            // The managed Ping-with-TTL traceroute cannot bind a source, exactly as the
            // managed ping path cannot. Tracing out the default route would attribute
            // another WAN's path to this one, so say so instead of tracing anyway.
            if (!string.IsNullOrEmpty(target.SourceInterface))
                return FailTrace(target, "Source-bound traceroute needs the native traceroute binary (Linux/macOS)");

            using var managedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            managedCts.CancelAfter(deadlineDuration);
            return await _managedTraceroute.RunAsync(target, Vantage, maxHops, perHopTimeout, 3, managedCts.Token);
        }

        var (exe, args, buildError) = BuildTracerouteCommand(target, maxHops, perHopTimeout, _tracerouteTraits);
        if (buildError != null)
            return FailTrace(target, buildError);
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
            // A never-completing read degrades to empty here on purpose: a partial or
            // absent traceroute parses to Reached=false - an honest failure, unlike
            // ping where empty output would fabricate a loss percentage.
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : (await SafeReadAsync(stdoutTask) ?? string.Empty);
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : (await SafeReadAsync(stderrTask) ?? string.Empty);

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

    /// <summary>
    /// Grace period for collecting a finished/killed child's redirected output.
    /// The pipe normally closes with the child, completing the read instantly;
    /// the bound exists because a pipe read whose completion is never delivered
    /// (wedged async engine) would otherwise hang its caller forever.
    /// </summary>
    private static readonly TimeSpan OutputReadGrace = TimeSpan.FromSeconds(5);

    private static async Task<string?> SafeReadAsync(Task<string> readTask)
    {
        var winner = await Task.WhenAny(readTask, Task.Delay(OutputReadGrace));
        if (winner != readTask)
        {
            // Never completed: the output is UNKNOWN, not empty - callers must
            // treat null as "drop this sample", never parse it as a result.
            Observe(readTask);
            return null;
        }
        try { return await readTask; }
        catch { return string.Empty; }
    }

    /// <summary>Observes a possibly never-completing task's fault so it can't surface as unobserved.</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

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
    /// <summary>
    /// Turns a probe source value into the address a TCP socket can bind to: an IP
    /// literal is taken as-is, an interface name is resolved to that interface's
    /// current IPv4 address through <paramref name="interfaceAddresses"/>.
    ///
    /// An interface with no IPv4 address returns an error rather than a null bind.
    /// Probing unbound would leave by the default route and record another WAN's
    /// latency under this one's name, which reads as data rather than as a failure.
    /// </summary>
    /// <param name="source">Source IP or interface name from the WAN context.</param>
    /// <param name="interfaceAddresses">Looks up an interface's addresses by name; empty when it has none or does not exist.</param>
    /// <returns>The address to bind, or null with the reason the probe cannot run.</returns>
    internal static (System.Net.IPAddress? Address, string? Error) ResolveTcpBindAddress(
        string source,
        Func<string, IReadOnlyList<System.Net.IPAddress>> interfaceAddresses)
    {
        if (System.Net.IPAddress.TryParse(source, out var literal))
            return (literal, null);

        if (!IsSafeSourceValue(source))
            return (null, $"Invalid probe source '{source}'");

        var addresses = interfaceAddresses(source);
        var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (ipv4 != null)
            return (ipv4, null);

        return (null, $"Interface '{source}' has no IPv4 address to bind the TCP probe to");
    }

    /// <summary>Current unicast IPv4/IPv6 addresses of a local interface by name; empty when there is no such interface.</summary>
    private static IReadOnlyList<System.Net.IPAddress> LookupInterfaceIPv4(string interfaceName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, interfaceName, StringComparison.OrdinalIgnoreCase));
            if (nic == null) return Array.Empty<System.Net.IPAddress>();
            return nic.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<System.Net.IPAddress>();
        }
    }

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

    private TracerouteResult FailTrace(ProbeTarget target, string error) => new()
    {
        Target = target,
        Vantage = Vantage,
        ModeUsed = target.Mode,
        Hops = Array.Empty<TraceHop>(),
        Reached = false,
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

    /// <summary>
    /// Builds the traceroute invocation for a target, including the source bind a WAN context
    /// asks for: an IP literal becomes <c>-s</c>, an interface name becomes <c>-i</c>, mirroring
    /// the ping path's <c>-I</c>/<c>-S</c>/<c>-b</c> handling. Returns an error instead of a
    /// command whenever the bind cannot be honored - an unbound trace would map another WAN's
    /// upstream onto this one, which reads as a discovery result rather than as a failure.
    /// </summary>
    /// <param name="target">Probe target; its SourceInterface carries the context's bind, if any.</param>
    /// <param name="maxHops">TTL ceiling.</param>
    /// <param name="perHopTimeout">Per-hop wait; floored at one second, which is the flag's unit.</param>
    /// <param name="traits">What the installed binary can bind, from <see cref="InterpretTracerouteBanner"/>.</param>
    /// <param name="isWindows">Which platform's traceroute to build for; defaults to this host's.</param>
    /// <returns>The executable and arguments, or an error explaining why the probe cannot run.</returns>
    internal static (string Exe, string Args, string? Error) BuildTracerouteCommand(
        ProbeTarget target, int maxHops, TimeSpan? perHopTimeout, TracerouteBinaryTraits traits, bool? isWindows = null)
    {
        var wait = (int)Math.Max(1, (perHopTimeout ?? TimeSpan.FromSeconds(2)).TotalSeconds);
        if (isWindows ?? OperatingSystem.IsWindows())
        {
            // tracert.exe has no source option at all, so a bound probe cannot run here.
            if (!string.IsNullOrEmpty(target.SourceInterface))
                return ("tracert.exe", string.Empty, "Source-bound traceroute needs the native traceroute binary (Linux/macOS)");
            // tracert: -h max hops, -w wait ms, -d no DNS resolution to speed up
            return ("tracert.exe", $"-h {maxHops} -w {wait * 1000} {target.Address}", null);
        }

        var sourceArg = string.Empty;
        if (!string.IsNullOrEmpty(target.SourceInterface))
        {
            if (!IsSafeSourceValue(target.SourceInterface))
                return ("traceroute", string.Empty, $"Invalid probe source '{target.SourceInterface}'");

            var isAddress = System.Net.IPAddress.TryParse(target.SourceInterface, out _);
            if (isAddress && !traits.CanBindAddress)
                return ("traceroute", string.Empty,
                    "This host's traceroute takes no source address, so the probe would go out the default route");
            if (!isAddress && !traits.CanBindInterface)
                return ("traceroute", string.Empty,
                    $"This host's traceroute takes no source interface, so the probe would not go out '{target.SourceInterface}'");

            sourceArg = isAddress
                ? $"-s {target.SourceInterface} "
                : $"-i {target.SourceInterface} ";
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
        var args = $"-m {maxHops} -q 2 -w {wait} {protoFlag} {sourceArg}{target.Address}".Trim();
        return ("traceroute", args, null);
    }
}
