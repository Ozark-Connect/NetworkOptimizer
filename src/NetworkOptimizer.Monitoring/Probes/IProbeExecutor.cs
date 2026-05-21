namespace NetworkOptimizer.Monitoring.Probes;

/// <summary>
/// Contract for running probes from a specific vantage point. Implementations exist for
/// the local server (.NET ICMP/TCP/process) and for SSH-able remote devices.
///
/// Every implementation must be tolerant of partial output and varying tool flavors —
/// UniFi devices ship busybox ping/traceroute with stripped flag sets and a terser output
/// format than standard Linux iputils (spec 5.1).
/// </summary>
public interface IProbeExecutor
{
    /// <summary>Identifies the vantage this executor runs probes from.</summary>
    ProbeVantage Vantage { get; }

    /// <summary>
    /// Detect (or recall from cache) what probe operations are available here. Should be
    /// cheap to call repeatedly — implementations cache after the first run.
    /// </summary>
    Task<ProbeCapability> GetCapabilityAsync(CancellationToken ct = default);

    /// <summary>Run a ping burst against the target.</summary>
    Task<PingProbeResult> PingAsync(
        ProbeTarget target,
        int count = 10,
        TimeSpan? perPingTimeout = null,
        CancellationToken ct = default);

    /// <summary>Run a TCP-connect probe.</summary>
    Task<TcpProbeResult> TcpProbeAsync(
        ProbeTarget target,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>Run a traceroute, preferring the mode in target.Mode (with implementation-specific fallback).</summary>
    Task<TracerouteResult> TracerouteAsync(
        ProbeTarget target,
        int maxHops = 30,
        TimeSpan? perHopTimeout = null,
        CancellationToken ct = default);
}
