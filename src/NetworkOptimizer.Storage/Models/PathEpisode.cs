namespace NetworkOptimizer.Storage.Models;

/// <summary>What a path episode's evidence says about the return path (spec 6).</summary>
public enum ReturnPathFinding
{
    /// <summary>Nothing about the return path could be read or stood out.</summary>
    None = 0,
    /// <summary>The destination's reply came home by a different number of hops during the episode than after it,
    /// with the forward path unchanged: a return-path change.</summary>
    ReturnHopCountChanged = 1,
    /// <summary>Hops at or past the onset reply by a hop count far from their forward position.</summary>
    AsymmetricHops = 2,
    /// <summary>Several destinations degraded together with no forward hop in common past the last clean one,
    /// so the common factor is not on the forward path.</summary>
    NoSharedForwardHop = 3
}

/// <summary>Why a target is on an episode.</summary>
public enum PathEpisodeTargetRole
{
    /// <summary>A degraded destination the episode traced.</summary>
    Degraded = 0,
    /// <summary>A clean destination on the same WAN, traced once for comparison.</summary>
    Control = 1
}

/// <summary>Which round a trace was.</summary>
public enum PathEpisodeTraceKind
{
    /// <summary>A degraded target, while degraded.</summary>
    Path = 0,
    /// <summary>The clean control, once on open.</summary>
    Control = 1,
    /// <summary>A degraded target after the episode cleared: the per-hop baseline.</summary>
    Recovery = 2,
    /// <summary>From a vantage toward the site, when one exists (not produced yet).</summary>
    Reverse = 3
}

/// <summary>
/// One Path Localization episode: an Internet or Custom target degraded against its own baseline while
/// the rest of the connection stayed clean, and the traceroutes taken from the vantage that measures it
/// while it lasted. One per WAN context at a time. The verdict fields mirror the ISP Health congestion
/// event they are new evidence for, so the scope and disposition read the same.
/// </summary>
public class PathEpisode
{
    public int Id { get; set; }

    /// <summary>The WAN context the episode ran on; null for the default path.</summary>
    public int? WanContextId { get; set; }

    /// <summary>The UniFi WAN key the context measures, when it has one, so the report for that WAN can find it.</summary>
    public string? WanInterface { get; set; }

    public DateTime OpenedAt { get; set; }

    /// <summary>When the degraded targets cleared (or the episode hit its time limit). Null while open.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>When the recovery round ran. Null until it has.</summary>
    public DateTime? RecoveryTracedAt { get; set; }

    /// <summary>JSON: the clean controls, the access layer's state, and the WAN load the gate saw.</summary>
    public string GateSnapshot { get; set; } = "{}";

    /// <summary>CongestionScope name: Hop, Segment, Asn, Corridor, Unlocalized.</summary>
    public string Scope { get; set; } = "Unlocalized";

    /// <summary>CongestionDisposition name: Confirmed, Unverifiable.</summary>
    public string Disposition { get; set; } = "Unverifiable";

    public string? BottleneckHopIp { get; set; }

    public int? BottleneckHopNumber { get; set; }

    public string? BottleneckLabel { get; set; }

    public ReturnPathFinding ReturnPathFinding { get; set; }

    /// <summary>One or two sentences on what the evidence says, for the event row.</summary>
    public string? Summary { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<PathEpisodeTarget> Targets { get; set; } = new();

    public List<PathEpisodeTrace> Traces { get; set; } = new();
}

/// <summary>A degraded or control target on an episode, with its readings and its own verdict.</summary>
public class PathEpisodeTarget
{
    public int Id { get; set; }

    public int EpisodeId { get; set; }

    public PathEpisode? Episode { get; set; }

    /// <summary>The MonitoringTargets row (Id, not TargetId).</summary>
    public int MonitoringTargetId { get; set; }

    public PathEpisodeTargetRole Role { get; set; }

    public double? BaselineRttMs { get; set; }

    public double? PeakRttMs { get; set; }

    public double? BaselineLossPct { get; set; }

    public double? PeakLossPct { get; set; }

    public string? Scope { get; set; }

    public string? Disposition { get; set; }

    public string? BottleneckHopIp { get; set; }

    public int? BottleneckHopNumber { get; set; }

    public string? BottleneckLabel { get; set; }
}

/// <summary>One traceroute round of an episode. Hops are kept as JSON: read whole, never queried across episodes.</summary>
public class PathEpisodeTrace
{
    public int Id { get; set; }

    public int EpisodeId { get; set; }

    public PathEpisode? Episode { get; set; }

    public int MonitoringTargetId { get; set; }

    public PathEpisodeTraceKind Kind { get; set; }

    public DateTime Timestamp { get; set; }

    /// <summary>The vantage the trace ran from ("server", "agent-3"): the one that measures the target.</summary>
    public string Vantage { get; set; } = "server";

    /// <summary>The bind the target's probes use (source IP or interface), or null for the default route.</summary>
    public string? SourceInterface { get; set; }

    public bool Reached { get; set; }

    /// <summary>What a hostname target resolved to for this trace, so the destination hop can be found
    /// again when the episode is read back. Null for an address target.</summary>
    public string? ResolvedAddress { get; set; }

    /// <summary>JSON array of the trace's hops, unanswered TTLs included.</summary>
    public string HopsJson { get; set; } = "[]";
}
