using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Monitoring.Probes;

// ProbeMode lives in NetworkOptimizer.Core.Enums so it can be persisted on the
// MonitoringTarget storage model without a duplicate enum / converter dance. Probe code
// uses the Core enum directly; this re-export keeps imports terse for call sites that
// only `using NetworkOptimizer.Monitoring.Probes`.

/// <summary>
/// Where the probe runs from. The NO server, or any SSH-able network device.
/// </summary>
public record ProbeVantage(string Id, VantageKind Kind, string? SshHost = null)
{
    public static ProbeVantage Server { get; } = new("server", VantageKind.Server);
}

public enum VantageKind
{
    Server = 0,
    SshDevice = 1
}

/// <summary>
/// What we're probing. Captures address + mode + optional port together so a target that
/// only responds to TCP:443 stays probed that way (spec 5.4).
/// </summary>
public record ProbeTarget(string Address, ProbeMode Mode, int? Port = null, string? SourceInterface = null)
{
    public override string ToString() => Port.HasValue
        ? $"{Mode.ToString().ToLowerInvariant()}://{Address}:{Port}"
        : $"{Mode.ToString().ToLowerInvariant()}://{Address}";
}

/// <summary>
/// Which probe operations a vantage point can actually perform. Capability detection runs
/// once per vantage and is cached (spec 3.2). On a UniFi device this is shaped by what
/// busybox ping/traceroute support; on the server it's shaped by the OS.
/// </summary>
public record ProbeCapability
{
    public required bool CanIcmpPing { get; init; }
    public required bool CanIcmpTraceroute { get; init; }
    public required bool CanUdpTraceroute { get; init; }
    public required bool CanTcpProbe { get; init; }
    public required bool IsBusyBoxPing { get; init; }
    public required bool IsBusyBoxTraceroute { get; init; }

    /// <summary>Human-readable summary for logs and UI debug views.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (CanIcmpPing) parts.Add(IsBusyBoxPing ? "ping (busybox)" : "ping");
        if (CanIcmpTraceroute) parts.Add(IsBusyBoxTraceroute ? "tracert-icmp (busybox)" : "tracert-icmp");
        if (CanUdpTraceroute) parts.Add("tracert-udp");
        if (CanTcpProbe) parts.Add("tcp-probe");
        return parts.Count == 0 ? "no probes available" : string.Join(", ", parts);
    }
}

/// <summary>Result of an ICMP/TCP ping probe burst.</summary>
public record PingProbeResult
{
    public required ProbeTarget Target { get; init; }
    public required ProbeVantage Vantage { get; init; }
    public required int Sent { get; init; }
    public required int Received { get; init; }
    public required DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public double? RttMinMs { get; init; }
    public double? RttAvgMs { get; init; }
    public double? RttMaxMs { get; init; }
    public double? JitterMs { get; init; }

    public string? ErrorMessage { get; init; }
    public string? RawOutput { get; init; }

    public bool Success => Received > 0;
    public double LossPercent => Sent == 0 ? 100.0 : Math.Max(0.0, (1.0 - (double)Received / Sent) * 100.0);
}

/// <summary>One hop on a traceroute. Address may be null for non-responding hops.</summary>
public record TraceHop
{
    public required int HopNumber { get; init; }
    public string? Address { get; init; }
    public string? Hostname { get; init; }
    public double? RttMinMs { get; init; }
    public double? RttAvgMs { get; init; }
    public double? RttMaxMs { get; init; }
    public int Probes { get; init; }
    public int Responses { get; init; }

    public bool Responded => Responses > 0 && Address != null;
}

public record TracerouteResult
{
    public required ProbeTarget Target { get; init; }
    public required ProbeVantage Vantage { get; init; }
    public required ProbeMode ModeUsed { get; init; }
    public required DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public required IReadOnlyList<TraceHop> Hops { get; init; }
    public bool Reached { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawOutput { get; init; }
}

/// <summary>Result of a single TCP-connect probe.</summary>
public record TcpProbeResult
{
    public required ProbeTarget Target { get; init; }
    public required ProbeVantage Vantage { get; init; }
    public required bool Connected { get; init; }
    public double? ConnectTimeMs { get; init; }
    public required DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? ErrorMessage { get; init; }
}
