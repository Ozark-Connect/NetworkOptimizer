using NetworkOptimizer.Core.Enums;

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

    /// <summary>
    /// Resolve a name, or a name for an address when <paramref name="reverse"/> is set, using
    /// whatever resolver this vantage is configured with. Which resolver answers is the point:
    /// two vantages on different WANs can legitimately disagree.
    /// </summary>
    /// <remarks>
    /// Defaults to unsupported so a vantage with no lookup path (and any test double) keeps
    /// compiling and says so plainly rather than returning an empty answer.
    /// </remarks>
    Task<DnsLookupResult> LookupAsync(
        ProbeTarget target,
        bool reverse = false,
        CancellationToken ct = default)
        => Task.FromResult(new DnsLookupResult
        {
            Target = target,
            Vantage = Vantage,
            Timestamp = DateTime.UtcNow,
            ErrorMessage = "This vantage cannot run DNS lookups."
        });

    /// <summary>Run a TCP-connect probe.</summary>
    Task<TcpProbeResult> TcpProbeAsync(
        ProbeTarget target,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>
    /// Run a traceroute in the requested mode. Total wall-clock time is bounded by
    /// <paramref name="totalDeadline"/> (default 10 s) so a probe whose every hop discards
    /// our packets can't hang the caller.
    /// </summary>
    Task<TracerouteResult> TracerouteAsync(
        ProbeTarget target,
        int maxHops = 30,
        TimeSpan? perHopTimeout = null,
        TimeSpan? totalDeadline = null,
        CancellationToken ct = default);
}
