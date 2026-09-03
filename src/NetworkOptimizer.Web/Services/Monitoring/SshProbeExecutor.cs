using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Runs probes from a SSH-able network device (gateway, switch, AP). This is the "vantage
/// change" primitive — letting the user run a traceroute from the gateway to isolate
/// LAN-side vs. server-side issues (spec 5.3).
///
/// UniFi devices ship busybox ping/traceroute with stripped flag sets, so capability is
/// probe-and-cache per device: the executor runs `ping -h` / `traceroute -h` once and
/// parses what the help output reveals.
/// </summary>
public class SshProbeExecutor : IProbeExecutor
{
    private readonly SshClientService _ssh;
    private readonly SshConnectionInfo _connection;
    private readonly ILogger<SshProbeExecutor> _logger;
    private ProbeCapability? _capability;
    private readonly SemaphoreSlim _capabilityLock = new(1, 1);

    public SshProbeExecutor(
        SshClientService ssh,
        SshConnectionInfo connection,
        string vantageId,
        ILogger<SshProbeExecutor> logger)
    {
        _ssh = ssh;
        _connection = connection;
        _logger = logger;
        Vantage = new ProbeVantage(vantageId, VantageKind.SshDevice, connection.Host);
    }

    public ProbeVantage Vantage { get; }

    public async Task<ProbeCapability> GetCapabilityAsync(CancellationToken ct = default)
    {
        if (_capability != null) return _capability;
        await _capabilityLock.WaitAsync(ct);
        try
        {
            if (_capability != null) return _capability;

            // `ping -h` and `traceroute -h` print help on both standard Linux and busybox,
            // typically with non-zero exit. The signal we want is the help text content.
            var pingHelp = await _ssh.ExecuteCommandAsync(_connection, "ping -h 2>&1 || true", TimeSpan.FromSeconds(5), ct);
            var traceHelp = await _ssh.ExecuteCommandAsync(_connection, "traceroute -h 2>&1 || true", TimeSpan.FromSeconds(5), ct);

            var traceOut0 = (traceHelp.Output + traceHelp.Error).ToLowerInvariant();
            if (traceHelp.ExitCode == 127 || traceOut0.Contains("not found"))
            {
                _logger.LogDebug("traceroute not found on {Vantage}, attempting apt-get update + install", Vantage.Id);
                // Refresh the package index first - a console with stale/empty apt lists can't
                // resolve the package otherwise. update runs unconditionally before install (;),
                // and the whole thing stays non-fatal (|| true) so a device without apt just
                // falls through to the capability re-probe below.
                await _ssh.ExecuteCommandAsync(_connection,
                    "apt-get update 2>/dev/null; apt-get install -y traceroute 2>/dev/null || true",
                    TimeSpan.FromSeconds(60), ct);
                traceHelp = await _ssh.ExecuteCommandAsync(_connection, "traceroute -h 2>&1 || true", TimeSpan.FromSeconds(5), ct);
            }

            var pingOut = (pingHelp.Output + pingHelp.Error).ToLowerInvariant();
            var traceOut = (traceHelp.Output + traceHelp.Error).ToLowerInvariant();

            bool pingBusyBox = pingOut.Contains("busybox") || pingOut.Contains("[-w deadline]") == false && pingOut.Contains("[-c count]");
            bool traceBusyBox = traceOut.Contains("busybox") || (!traceOut.Contains("--icmp") && traceOut.Contains("[-n]"));

            // Standard Linux traceroute exposes -I (ICMP), -T (TCP). Busybox build does too on
            // newer firmware but may lack -T. Detect by flag mentions in the help text.
            bool canIcmpTrace = traceOut.Contains("-i") || traceOut.Contains("icmp");
            bool canUdpTrace = !string.IsNullOrWhiteSpace(traceOut);
            bool canTcpTrace = traceOut.Contains("-t") || traceOut.Contains("tcp");

            _capability = new ProbeCapability
            {
                CanIcmpPing = pingHelp.ExitCode != 127 && !pingOut.Contains("not found"),
                CanIcmpTraceroute = canIcmpTrace && traceHelp.ExitCode != 127 && !traceOut.Contains("not found"),
                CanUdpTraceroute = canUdpTrace && traceHelp.ExitCode != 127 && !traceOut.Contains("not found"),
                CanTcpProbe = canTcpTrace,
                IsBusyBoxPing = pingBusyBox,
                IsBusyBoxTraceroute = traceBusyBox
            };

            _logger.LogDebug(
                "SSH probe capabilities for {Vantage}: {Caps}",
                Vantage.Id, _capability.Describe());

            return _capability;
        }
        finally
        {
            _capabilityLock.Release();
        }
    }

    public async Task<PingProbeResult> PingAsync(
        ProbeTarget target,
        int count = 10,
        TimeSpan? perPingTimeout = null,
        CancellationToken ct = default)
    {
        // TCP "ping" is repeated TCP connects to the port, not ICMP. Run them on the
        // vantage itself so the result reflects that host's path (bash /dev/tcp), which
        // is what the UI promises when TCP mode is selected.
        if (target.Mode == ProbeMode.Tcp)
            return await TcpPingViaSshAsync(target, count, perPingTimeout ?? TimeSpan.FromSeconds(2), ct);

        var cap = await GetCapabilityAsync(ct);
        if (!cap.CanIcmpPing)
        {
            return new PingProbeResult
            {
                Target = target,
                Vantage = Vantage,
                Sent = count,
                Received = 0,
                ErrorMessage = "ping not available on this vantage",
                Timestamp = DateTime.UtcNow
            };
        }

        var timeoutSec = Math.Max(1, (int)(perPingTimeout?.TotalSeconds ?? 2));
        // -c count -W timeout. The deadline flag (-w) is iputils-only; -W is supported in both
        // iputils and busybox. -I binds to a specific interface for WAN selection.
        var ifaceFlag = !string.IsNullOrEmpty(target.SourceInterface) ? $"-I {ShellEscape(target.SourceInterface)} " : "";
        var cmd = $"ping {ifaceFlag}-c {count} -W {timeoutSec} {ShellEscape(target.Address)}";

        var result = await _ssh.ExecuteCommandAsync(_connection, cmd, TimeSpan.FromSeconds(timeoutSec * count + 10), ct);
        var combined = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
        return PingOutputParser.Parse(combined ?? string.Empty, target, Vantage, count);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// stdout and stderr are merged because several busybox builds print the whole answer to
    /// stderr, and one prints a can't-resolve line there even when the lookup succeeded.
    /// </remarks>
    public async Task<DnsLookupResult> LookupAsync(
        ProbeTarget target,
        bool reverse = false,
        CancellationToken ct = default)
    {
        var cmd = $"nslookup {ShellEscape(target.Address)} 2>&1";
        var result = await _ssh.ExecuteCommandAsync(_connection, cmd, TimeSpan.FromSeconds(15), ct);
        var combined = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;

        if (string.IsNullOrWhiteSpace(combined))
            return new DnsLookupResult
            {
                Kind = NslookupOutputParser.ResultKind,
                Target = target,
                Vantage = Vantage,
                Timestamp = DateTime.UtcNow,
                ErrorMessage = "No output from nslookup on this device"
            };

        return NslookupOutputParser.Parse(combined, target, Vantage, reverse);
    }

    public async Task<TcpProbeResult> TcpProbeAsync(
        ProbeTarget target,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var port = target.Port ?? 443;
        var timeoutSec = Math.Max(1, (int)(timeout?.TotalSeconds ?? 2));

        if (!System.Net.IPAddress.TryParse(target.Address, out _))
        {
            return new TcpProbeResult
            {
                Target = target,
                Vantage = new NetworkOptimizer.Monitoring.Probes.ProbeVantage(_connection.Host ?? "unknown", NetworkOptimizer.Monitoring.Probes.VantageKind.SshDevice, _connection.Host),
                Connected = false,
                Timestamp = DateTime.UtcNow,
                ErrorMessage = "Invalid IP address"
            };
        }

        var cmd = $"timeout {timeoutSec} bash -c '(echo > /dev/tcp/{ShellEscape(target.Address)}/{port}) 2>/dev/null && echo OK || echo FAIL'";
        var result = await _ssh.ExecuteCommandAsync(_connection, cmd, TimeSpan.FromSeconds(timeoutSec + 5), ct);
        var output = (result.Output ?? string.Empty).Trim();

        if (output.Contains("OK"))
        {
            return new TcpProbeResult
            {
                Target = target,
                Vantage = Vantage,
                Connected = true,
                Timestamp = DateTime.UtcNow
            };
        }

        return new TcpProbeResult
        {
            Target = target,
            Vantage = Vantage,
            Connected = false,
            ErrorMessage = string.IsNullOrEmpty(output) ? "TCP probe failed" : output,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// TCP "ping" from an SSH vantage: does <paramref name="count"/> real TCP connects to
    /// the target port on the remote host via bash <c>/dev/tcp</c>, timing each with
    /// <c>date</c>+<c>awk</c> so the RTT is the vantage's own connect time (not our SSH
    /// round-trip). Emits one line per attempt - "OK &lt;ms&gt;", "OK" (no timer on this
    /// host, e.g. busybox <c>date</c> without <c>%N</c>), or "FAIL" - which we aggregate
    /// into a ping result. Reachability always works; RTT degrades gracefully.
    /// </summary>
    private async Task<PingProbeResult> TcpPingViaSshAsync(ProbeTarget target, int count, TimeSpan timeout, CancellationToken ct)
    {
        var port = target.Port ?? 443;
        var timeoutSec = Math.Max(1, (int)timeout.TotalSeconds);

        // The address lands inside a nested `bash -c '...'`, where ShellEscape's own quotes
        // close the outer one and hand everything after it to the login shell. Validate the
        // address instead of escaping it: an IP or hostname is all this can ever be.
        var (addressIsValid, addressError) = InputSanitizer.ValidatePingHost(target.Address);
        if (!addressIsValid)
        {
            return new PingProbeResult
            {
                Target = target,
                Vantage = Vantage,
                Sent = count,
                Received = 0,
                ErrorMessage = addressError ?? "Invalid target address",
                Timestamp = DateTime.UtcNow
            };
        }

        var addr = target.Address;

        var cmd =
            $"n=0; while [ $n -lt {count} ]; do n=$((n+1)); " +
            $"s=$(date +%s.%N 2>/dev/null); " +
            $"if timeout {timeoutSec} bash -c '(echo > /dev/tcp/{addr}/{port})' 2>/dev/null; then " +
            $"e=$(date +%s.%N 2>/dev/null); " +
            $"awk -v s=$s -v e=$e 'BEGIN{{d=(e-s)*1000; if(d>0&&d<60000)printf \"OK %.3f\\n\",d; else print \"OK\"}}'; " +
            $"else echo FAIL; fi; " +
            $"sleep 0.2 2>/dev/null; done";

        var result = await _ssh.ExecuteCommandAsync(_connection, cmd, TimeSpan.FromSeconds(timeoutSec * count + 15), ct);
        var lines = (result.Output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rtts = new List<double>();
        var received = 0;
        foreach (var line in lines)
        {
            if (!line.StartsWith("OK", StringComparison.Ordinal)) continue;
            received++;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
                rtts.Add(ms);
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
            ErrorMessage = received == 0
                ? $"No TCP connect to port {port} (closed/filtered, or bash /dev/tcp unavailable on this vantage)"
                : null,
            Timestamp = DateTime.UtcNow
        };
    }

    private static double StdDev(IReadOnlyCollection<double> v)
    {
        var mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
    }

    public async Task<TracerouteResult> TracerouteAsync(
        ProbeTarget target,
        int maxHops = 30,
        TimeSpan? perHopTimeout = null,
        TimeSpan? totalDeadline = null,
        CancellationToken ct = default)
    {
        var cap = await GetCapabilityAsync(ct);
        if (!cap.CanIcmpTraceroute && !cap.CanUdpTraceroute)
        {
            return new TracerouteResult
            {
                Target = target,
                Vantage = Vantage,
                ModeUsed = target.Mode,
                Hops = Array.Empty<TraceHop>(),
                Reached = false,
                ErrorMessage = "traceroute not available on this vantage",
                Timestamp = DateTime.UtcNow
            };
        }

        var waitSec = Math.Max(1, (int)(perHopTimeout?.TotalSeconds ?? 2));
        string modeFlag = target.Mode switch
        {
            ProbeMode.Icmp when cap.CanIcmpTraceroute => "-I",
            ProbeMode.Tcp when cap.CanTcpProbe => $"-T -p {target.Port ?? 80}",
            _ => string.Empty
        };

        var parts = new List<string> { "traceroute", $"-m {maxHops}", $"-w {waitSec}" };
        if (!string.IsNullOrEmpty(modeFlag)) parts.Add(modeFlag);
        if (!string.IsNullOrEmpty(target.SourceInterface)) parts.Add($"-i {ShellEscape(target.SourceInterface)}");
        parts.Add(ShellEscape(target.Address));
        var cmd = string.Join(" ", parts);
        var overall = totalDeadline ?? TimeSpan.FromSeconds(10);
        var result = await _ssh.ExecuteCommandAsync(_connection, cmd, overall, ct);

        var output = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;
        return TracerouteOutputParser.Parse(output ?? string.Empty, target, Vantage, target.Mode);
    }

    /// <summary>
    /// Quotes a value for one level of shell quoting. Never use it inside a nested
    /// <c>bash -c '...'</c>: its quotes terminate the enclosing ones and the remainder runs in
    /// the outer shell. Validate the value there instead (see TcpPingViaSshAsync, TcpProbeAsync).
    /// </summary>
    private static string ShellEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        if (!s.Any(c => char.IsWhiteSpace(c) || "$`\\\"'!|&;<>(){}[]?*".Contains(c)))
            return s;
        return "'" + s.Replace("'", "'\\''") + "'";
    }
}
