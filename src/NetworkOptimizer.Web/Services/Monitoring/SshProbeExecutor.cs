using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
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

    private static string ShellEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        if (!s.Any(c => char.IsWhiteSpace(c) || "$`\\\"'!|&;<>(){}[]?*".Contains(c)))
            return s;
        return "'" + s.Replace("'", "'\\''") + "'";
    }
}
