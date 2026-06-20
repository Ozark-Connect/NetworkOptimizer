namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>Readiness of the ISP Health pipeline, used by the live view tiles.</summary>
public enum IspHealthStatus
{
    /// <summary>Monitoring or InfluxDB is not configured.</summary>
    NotConfigured,
    /// <summary>No enabled ISP or transit targets; Upstream Discovery has not been run.</summary>
    NeedsDiscovery,
    /// <summary>Targets exist but no access technology has been selected.</summary>
    NeedsTechnology,
    /// <summary>Monitoring is set up but has not collected enough hours of data yet.</summary>
    InsufficientData,
    /// <summary>Prerequisites met; first computation has not finished yet.</summary>
    Computing,
    /// <summary>A report is available.</summary>
    Ready
}

/// <summary>Severity of an ISP Health issue or recommendation.</summary>
public enum IspIssueSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>Direction of a detected path RTT step change.</summary>
public enum PathShiftDirection
{
    Up,
    Down
}

/// <summary>One scored factor inside a dimension (e.g. idle latency).</summary>
public class IspScoreFactor
{
    public required string Name { get; init; }

    /// <summary>0-100, or null when there was not enough data to score this factor.</summary>
    public int? Score { get; init; }

    /// <summary>Relative weight within the dimension before renormalization.</summary>
    public double Weight { get; init; }

    /// <summary>Human-readable measured value, e.g. "2.4 ms" or "0.03%".</summary>
    public string? ValueText { get; init; }

    /// <summary>What was measured and against which expectation.</summary>
    public string? Description { get; init; }
}

/// <summary>One of the three top-level score dimensions.</summary>
public class IspScoreDimension
{
    public required string Name { get; init; }

    /// <summary>0-100, or null when no factor in the dimension had data.</summary>
    public int? Score { get; init; }

    /// <summary>Relative weight in the overall score before renormalization.</summary>
    public double Weight { get; init; }

    public List<IspScoreFactor> Factors { get; init; } = new();
}

/// <summary>Per-ASN health grade used by the transit and ISP ASN dimensions.</summary>
public class IspAsnHealth
{
    public int AsnNumber { get; init; }
    public string? AsnName { get; init; }
    public List<string> TargetIds { get; init; } = new();

    /// <summary>Median RTT of the graded hop/cluster (used for the grade and dimension factor).</summary>
    public double? MedianRttMs { get; init; }

    /// <summary>Mean RTT across all of the ASN's monitored hops (shown on the Networks on Your Path card).</summary>
    public double? MeanRttMs { get; init; }

    /// <summary>Lowest and highest hop RTT in the ASN, for the ISP card's RTT range.</summary>
    public double? MinRttMs { get; init; }
    public double? MaxRttMs { get; init; }

    public double? P95RttMs { get; init; }
    public double? MedianJitterMs { get; init; }
    public double? P95JitterMs { get; init; }

    /// <summary>True when the displayed jitter was assimilated from elsewhere (a cleaner
    /// farther cluster for transit, or the cleanest transit ASN for the ISP) rather than
    /// this network's own nearest reading. Drives the info icon on the card.</summary>
    public bool JitterAssimilated { get; init; }

    /// <summary>This network's own measured jitter before assimilation, for the tooltip.</summary>
    public double? RawJitterMs { get; init; }
    public double? RttMadMs { get; init; }
    public double? LossPct { get; init; }

    /// <summary>Median RTT beyond the first clean ISP hop. Null for ISP ASNs.</summary>
    public double? ReachDeltaMs { get; init; }

    public int? LatencyStabilityScore { get; init; }
    public int? JitterScore { get; init; }
    public int? LossScore { get; init; }

    /// <summary>The reach ceiling: best grade this ASN's distance allows. Null for ISP ASNs.</summary>
    public int? ReachLatencyScore { get; init; }

    public int? CongestionScore { get; init; }
    public int? OverallScore { get; init; }
    public int CongestionEventCount { get; init; }
}

/// <summary>One monitored ISP-network target, broken out on the ISP Network card.</summary>
public class IspTargetHealth
{
    public required string TargetId { get; init; }
    public required string Name { get; init; }

    /// <summary>Displayed RTT: winsorized mean over the window.</summary>
    public double? RttMs { get; init; }

    /// <summary>Effective (absolved) P95 jitter this hop is graded on.</summary>
    public double? P95JitterMs { get; init; }

    /// <summary>This hop's own measured P95 jitter, before any absolve.</summary>
    public double? RawJitterMs { get; init; }

    /// <summary>True when a cleaner witness (transit/sibling/destination) pulled this hop's jitter below its own reading.</summary>
    public bool JitterAssimilated { get; init; }

    public double? LossPct { get; init; }

    /// <summary>Per-hop quality grade (stability, jitter, loss, congestion, intra-ASN reach).</summary>
    public int? OverallScore { get; init; }

    /// <summary>RTT beyond this ASN's nearest hop (0 for the nearest). Drives the soft reach ceiling.</summary>
    public double? ReachDeltaMs { get; init; }

    /// <summary>True for the first clean hop, the target the access layer idle latency comes from.</summary>
    public bool IsGradedHop { get; init; }
}

/// <summary>An actionable finding or recommendation surfaced on the ISP Health tab.</summary>
public class IspHealthIssue
{
    public IspIssueSeverity Severity { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? Recommendation { get; init; }
    public string? LinkUrl { get; init; }
    public string? LinkText { get; init; }
}

/// <summary>
/// How precisely a congestion event was localized, most specific first. The localizer
/// reports the highest tier the trace map and clean controls affirmatively support and
/// stops there, recording in <see cref="CongestionEvent.AttributionReason"/> why it could
/// not go deeper - it never collapses to <see cref="Unlocalized"/> just because the exact
/// hop was ambiguous, and never asserts <see cref="Hop"/> without the proof.
/// </summary>
public enum CongestionScope
{
    /// <summary>Pinned to a single bottleneck hop: the deepest hop all affected traced targets route through, with clean off-path controls.</summary>
    Hop,
    /// <summary>Isolated to the link between the last clean hop and the first elevated hop, but the far end is not a single hop.</summary>
    Segment,
    /// <summary>Attributed to one ASN's network (its hops elevated, its upstream clean) but not a single hop.</summary>
    Asn,
    /// <summary>Narrowed to the set of affected paths/corridors, but the bottleneck link could not be isolated.</summary>
    Corridor,
    /// <summary>Could not be localized; a shared elevation across networks. Lowest confidence.</summary>
    Unlocalized
}

/// <summary>What the localizer concluded a congestion candidate actually is, after the
/// downstream-propagation and load-correlation gates.</summary>
public enum CongestionDisposition
{
    /// <summary>Real congestion: the elevation is the deepest hop and/or propagated to proven-downstream traced targets.</summary>
    Confirmed,
    /// <summary>Self-inflicted access-egress bufferbloat: bottleneck at the local access egress while heavy local WAN load coincided. Not external congestion.</summary>
    SelfInflicted,
    /// <summary>Near-hop elevation that did NOT propagate to proven-downstream targets - ICMP/control-plane deprioritization, not a forwarding bottleneck. The transit farther-cluster absolve, applied to detection.</summary>
    ControlPlaneNoise,
    /// <summary>Elevated, but no traced downstream target exists to confirm or refute it (e.g. a dead-end transit hop). Reported at low confidence, never asserted.</summary>
    Unverifiable
}

/// <summary>
/// A sustained latency and jitter elevation. After detection each event is run through the
/// localizer, which attributes it to a bottleneck (<see cref="Scope"/>) and decides what it
/// is (<see cref="Disposition"/>): real congestion, self-inflicted bufferbloat, absolved
/// control-plane noise, or unverifiable. Co-temporal per-ASN candidates are merged only when
/// they share a bottleneck on the trace map, not merely because they overlap in time.
/// </summary>
public class CongestionEvent
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }

    /// <summary>ASNs affected. More than one means a shared upstream event.</summary>
    public List<int> AsnNumbers { get; init; } = new();

    public List<string> AsnNames { get; init; } = new();

    /// <summary>
    /// The monitored targets this event fired on. Used to attribute the event to the
    /// right card by role: the same ASN can be both the access ISP and a transit
    /// provider (e.g. AS7018), and a transit-side event must not credit the ISP card.
    /// </summary>
    public List<string> TargetIds { get; init; } = new();
    public double BaselineRttMs { get; init; }
    public double PeakRttMs { get; init; }
    public double BaselineJitterMs { get; init; }
    public double PeakJitterMs { get; init; }

    /// <summary>The specificity tier the localizer could defend for this event.</summary>
    public CongestionScope Scope { get; set; } = CongestionScope.Unlocalized;

    /// <summary>What the localizer concluded this elevation actually is.</summary>
    public CongestionDisposition Disposition { get; set; } = CongestionDisposition.Confirmed;

    /// <summary>IP of the attributed bottleneck hop, when <see cref="Scope"/> is <see cref="CongestionScope.Hop"/>.</summary>
    public string? BottleneckHopIp { get; set; }

    /// <summary>Human-readable bottleneck label (hop name, segment, ASN, or corridor) for the report.</summary>
    public string? BottleneckLabel { get; set; }

    /// <summary>True when the event overlapped heavy local WAN load (input to the self-inflicted gate).</summary>
    public bool LoadCoincident { get; set; }

    /// <summary>
    /// How many other monitored paths (different routes/networks, and the access hops ahead of
    /// the bottleneck) stayed clean during this event. A non-zero count under load is the proof
    /// the elevation is this hop's own capacity, not access-layer bufferbloat - which would lift
    /// every path that shares your access link.
    /// </summary>
    public int CleanParallelPaths { get; set; }

    /// <summary>True when this hop's congestion was confirmed indirectly by a sibling hop on the
    /// same ASN congesting in the same window, rather than by its own downstream propagation
    /// (used for a dead-end hop whose confirmed sibling proves the network is really degrading).</summary>
    public bool ConfirmedBySibling { get; set; }

    /// <summary>Localizer confidence 0-100; lowest for <see cref="CongestionDisposition.Unverifiable"/> / <see cref="CongestionScope.Unlocalized"/>.</summary>
    public int Confidence { get; set; } = 50;

    /// <summary>Why the localizer stopped at this tier or reached this disposition - doubles as a coverage to-do (e.g. "no downstream probe past this hop").</summary>
    public string? AttributionReason { get; set; }

    /// <summary>True when this event must not penalize the score: self-inflicted bufferbloat or absolved control-plane noise.</summary>
    public bool Suppressed => Disposition is CongestionDisposition.SelfInflicted or CongestionDisposition.ControlPlaneNoise;

    public bool IsShared => AsnNumbers.Count > 1;
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// A sustained step up or down in path RTT, indicating a BGP or transport fabric
/// shift. Informational only; never affects the score.
/// </summary>
public class PathShiftEvent
{
    public DateTime Time { get; init; }
    public int? AsnNumber { get; init; }
    public string? AsnName { get; init; }
    public string? TargetId { get; init; }
    public double BeforeMedianMs { get; init; }
    public double AfterMedianMs { get; init; }
    public double DeltaMs => AfterMedianMs - BeforeMedianMs;
    public PathShiftDirection Direction => AfterMedianMs >= BeforeMedianMs ? PathShiftDirection.Up : PathShiftDirection.Down;

    /// <summary>Number of targets showing a correlated step at the same boundary.</summary>
    public int CorrelatedTargetCount { get; init; } = 1;

    /// <summary>True when this shift came from an internet/CDN destination (by DB TargetType),
    /// not an on-path ISP/transit hop. Correlation prefers a non-destination as the label.</summary>
    public bool IsDestination { get; init; }
}

/// <summary>Whether the access/first hop itself went dark, or only everything beyond it.</summary>
public enum OutageScope
{
    /// <summary>Even the access/first ISP hop stopped responding - the whole WAN went dark.</summary>
    FullWan,

    /// <summary>The access hop stayed reachable while transit and the internet went dark - the break sat upstream of it.</summary>
    Upstream,

    /// <summary>The LAN gateway itself was unreachable through the outage - a local LAN/switch/gateway
    /// outage, not the ISP's WAN. Surfaced but not score-affecting (the ISP isn't at fault).</summary>
    Local
}

/// <summary>
/// A period where the internet was unreachable: the destination/internet targets went to
/// near-total loss while probes kept reporting. A monitoring gap (console offline) has no
/// samples at all and is never an outage. Scored by duration alone via a capped Packet
/// Loss penalty - independent of shape or which hops dropped; the scope, break point, and
/// per-tier recovery shape are presentation only.
/// </summary>
public class OutageEvent
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public TimeSpan Duration => End - Start;

    /// <summary>Whether even the access hop went dark, or it held while everything beyond it dropped.</summary>
    public OutageScope Scope { get; init; }

    /// <summary>
    /// The deepest tier/hop that stayed reachable through the outage - the break sat just
    /// beyond it. Null when even the access hop went dark (a full WAN outage).
    /// </summary>
    public string? LastReachableHop { get; init; }

    /// <summary>Per-tier loss and recovery, nearest first, for the outage-shape display.</summary>
    public List<OutageTierState> Tiers { get; init; } = new();

    /// <summary>
    /// Points this outage contributed to the ISP Health penalty - its duration share of the
    /// total (curve-based) outage penalty. 0 for Local (LAN/gateway) outages, which are never
    /// scored. Set by the scorer so each outage row can show its own "-N points".
    /// </summary>
    public int ScorePenaltyPoints { get; set; }
}

/// <summary>One network tier's behavior during an outage, for the recovery-shape display.</summary>
public class OutageTierState
{
    public required string Name { get; init; }

    /// <summary>Distance rank: 0 = nearest (access/OLT), higher = farther out (transit, internet).</summary>
    public int Depth { get; init; }

    /// <summary>Worst mean loss this tier reached during the outage.</summary>
    public double PeakLossPct { get; init; }

    /// <summary>True when this tier reached the dark threshold at some point in the outage.</summary>
    public bool WentDark { get; init; }

    /// <summary>First time this tier fell back below the dark threshold after going dark; null if it never went dark or never recovered in-window.</summary>
    public DateTime? RecoveredAt { get; init; }
}

/// <summary>The full ISP Health report for the trailing window.</summary>
public class IspHealthReport
{
    public int OverallScore { get; init; }
    public DateTime ComputedAt { get; init; }
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }
    public required AccessProfile Profile { get; init; }
    public required IspScoreDimension AccessDimension { get; init; }
    public required IspScoreDimension TransitDimension { get; init; }
    public required IspScoreDimension IspAsnDimension { get; init; }
    public List<IspAsnHealth> TransitAsns { get; init; } = new();
    public List<IspAsnHealth> IspAsns { get; init; } = new();
    public List<IspTargetHealth> IspTargets { get; init; } = new();
    public List<IspHealthIssue> Issues { get; init; } = new();
    public List<CongestionEvent> CongestionEvents { get; init; } = new();
    public List<PathShiftEvent> PathShifts { get; init; } = new();
    public List<OutageEvent> Outages { get; init; } = new();

    /// <summary>False when expected WAN speeds were unavailable and loaded analysis was skipped.</summary>
    public bool HasExpectedSpeeds { get; init; }

    /// <summary>
    /// False when no upstream hop-ancestry (trace map) is persisted, so the per-hop jitter
    /// absolve gate runs in its lenient fallback. Prompts the user to re-run Upstream Discovery.
    /// </summary>
    public bool HasUpstreamTraceMap { get; init; }

    /// <summary>False when no loaded windows occurred in the window (line never under load).</summary>
    public bool HasLoadedSamples { get; init; }

    /// <summary>Expected plan speeds disclosed to the user, with where they came from.</summary>
    public double? ExpectedDownloadMbps { get; init; }
    public double? ExpectedUploadMbps { get; init; }
    public string? ExpectedSpeedSource { get; init; }

    /// <summary>Best WAN speed test result used by the Speed vs Plan factor.</summary>
    public double? MeasuredDownloadMbps { get; init; }
    public double? MeasuredUploadMbps { get; init; }

    /// <summary>Typical (median of trimmed) WAN speed over the window, shown beneath the best.</summary>
    public double? TypicalDownloadMbps { get; init; }
    public double? TypicalUploadMbps { get; init; }
    public DateTime? SpeedTestTime { get; init; }

    public static string GradeLabel(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 60 => "Fair",
        _ => "Poor"
    };
}

/// <summary>A point of latency series data used by the scorer and detectors.</summary>
public record LatencySample(DateTime Time, double? RttAvgMs, double? RttMaxMs, double? JitterMs, double? LossPercent)
{
    /// <summary>Jitter, falling back to the max-minus-avg RTT spread when the probe did not report jitter.</summary>
    public double? EffectiveJitterMs => JitterMs ?? (RttMaxMs.HasValue && RttAvgMs.HasValue ? RttMaxMs.Value - RttAvgMs.Value : null);
}

/// <summary>A per-ASN latency series assembled from one or more targets.</summary>
public class AsnSeries
{
    public int AsnNumber { get; init; }
    public string? AsnName { get; init; }
    public List<string> TargetIds { get; init; } = new();
    public List<LatencySample> Samples { get; init; } = new();

    /// <summary>
    /// Mean RTT across the ASN's full nearest cluster, for the Networks on Your Path
    /// card. On the ISP grading series this is wider than Samples (which is the single
    /// graded hop); on transit it equals the graded cluster. Display only.
    /// </summary>
    public double? NearestClusterMeanRttMs { get; init; }

    /// <summary>
    /// A farther cluster's samples used to absolve false near-hop jitter. A near hop often
    /// shows false jitter from ICMP deprioritization; a cleaner farther cluster, confirmed
    /// downstream by stored traceroute hop order, disproves it. Jitter and stability are
    /// graded on the BETTER (lower) of <see cref="Samples"/> and this (absolve-only: a
    /// jittery farther cluster never downgrades the nearer). RTT and reach always use
    /// Samples. Empty means no confirmed farther cluster, so Samples alone is used.
    /// </summary>
    public List<LatencySample> JitterSourceSamples { get; init; } = new();

    /// <summary>
    /// On a grading series, all of this ASN-role's target IDs (every hop, every
    /// cluster), used to attribute congestion to the correct card when the same ASN
    /// appears as both the access ISP and transit. Empty on chart-cluster series.
    /// </summary>
    public List<string> RoleTargetIds { get; init; } = new();

    /// <summary>The IPs of this series' targets, so a witness can be tested for routing through them.</summary>
    public List<string> HopIps { get; init; } = new();

    /// <summary>
    /// The monitored hop IPs proven upstream of this series (union over its targets, from the
    /// discovery traces). A witness series routes through a hop X - and so may absolve it -
    /// iff X's IP is in the witness's ancestor set. Empty for a first hop.
    /// </summary>
    public List<string> AncestorIps { get; init; } = new();

    /// <summary>True for an internet/CDN destination series (DB TargetType InternetService).
    /// Carried onto path-shift events so correlation can prefer an on-path hop as the label.</summary>
    public bool IsDestination { get; init; }
}

/// <summary>Load classification of one aggregate window.</summary>
public record LoadWindow(bool IsIdle, bool IsLoadedDown, bool IsLoadedUp);

/// <summary>A WAN throughput point joined into load classification.</summary>
public record ThroughputSample(DateTime Time, double? DownloadBps, double? UploadBps);

/// <summary>Everything the pure scorer needs; assembled by IspHealthService.</summary>
public class IspHealthInputs
{
    public DateTime WindowStart { get; init; }
    public DateTime WindowEnd { get; init; }

    /// <summary>Series of the first clean ISP hop (lowest-median enabled AccessIsp target).</summary>
    public List<LatencySample> FirstHopSeries { get; init; } = new();

    /// <summary>TargetId of the first clean hop, for marking it in the breakout.</summary>
    public string? FirstHopTargetId { get; init; }

    /// <summary>
    /// One series per public AccessIsp hop (RFC1918 / CPE-LAN-side excluded). Loaded
    /// latency takes the worst loaded delta across these, because access congestion can
    /// surface on any access hop (e.g. the OLT), not just the nearest one, and a given
    /// hop may miss a brief spike depending on probe timing.
    /// </summary>
    public List<List<LatencySample>> AccessHopSeries { get; init; } = new();

    /// <summary>One series per ISP target (AsnName carries the target name) for the breakout.</summary>
    public List<AsnSeries> IspTargetSeries { get; init; } = new();

    /// <summary>Per-target series pooled for packet loss (ISP + transit + anycast DNS targets).</summary>
    public List<List<LatencySample>> LossPoolSeries { get; init; } = new();

    /// <summary>Per-ASN series for transit targets.</summary>
    public List<AsnSeries> TransitAsnSeries { get; init; } = new();

    /// <summary>Per-ASN series for access ISP targets.</summary>
    public List<AsnSeries> IspAsnSeries { get; init; } = new();

    /// <summary>
    /// Per-target series for monitored internet/destination endpoints (anycast DNS, CDN
    /// probes). Each carries the hops proven upstream of it (AncestorIps) so a destination's
    /// clean end-to-end jitter can absolve an ISP hop it provably routes through - an
    /// ICMP-deprioritized hop whose forwarded traffic reaches the destination smoothly.
    /// </summary>
    public List<AsnSeries> DestinationSeries { get; init; } = new();

    /// <summary>WAN throughput over the window (primary WAN).</summary>
    public List<ThroughputSample> WanRates { get; init; } = new();

    /// <summary>
    /// Median RTT delta of the internet/CDN targets beyond the first clean ISP hop.
    /// Measures how far the internet is from this location, the rural/metro context
    /// the transit reach ceiling normalizes against.
    /// </summary>
    public double? InternetMedianDeltaMs { get; init; }

    public double? ExpectedDownloadMbps { get; init; }
    public double? ExpectedUploadMbps { get; init; }

    /// <summary>Where the expected speeds came from, for UI disclosure.</summary>
    public string? ExpectedSpeedSource { get; init; }

    /// <summary>Server/gateway WAN speed test results (client-initiated tests excluded), newest first.</summary>
    public List<SpeedTestSample> WanSpeedTests { get; init; } = new();

    /// <summary>Whether UniFi Smart Queues is already enabled on the WAN.</summary>
    public bool SmartQueuesEnabled { get; init; }

    /// <summary>
    /// Time windows to exclude from loaded-line analysis. Adaptive SQM speed probes
    /// briefly crank the HTB rate for an unshaped measurement; the resulting bufferbloat
    /// is real but does not represent the user's normal SQM-protected experience.
    /// </summary>
    public List<(DateTime Start, DateTime End)> LoadExclusionWindows { get; init; } = new();

    /// <summary>Pre-detected congestion events (post correlation pass).</summary>
    public List<CongestionEvent> CongestionEvents { get; init; } = new();

    /// <summary>Pre-detected path shift events. Informational only.</summary>
    public List<PathShiftEvent> PathShifts { get; init; } = new();

    /// <summary>Pre-detected internet-unreachable outages in the window.</summary>
    public List<OutageEvent> Outages { get; init; } = new();

    /// <summary>
    /// True when Upstream Discovery has persisted hop-ancestor data for this WAN. When false
    /// (never discovered, or pre-ancestor data), the jitter absolve gate falls open for
    /// transit (transit is always downstream of the ISP) and stays closed for ISP siblings.
    /// </summary>
    public bool HopOrderKnown { get; init; }
}

/// <summary>
/// One WAN speed test result. The latency fields are the test's own unloaded ping
/// and the loaded latency measured while each direction saturated, which serve as
/// the loaded-latency evidence when passive load windows are too sparse.
/// </summary>
public record SpeedTestSample(
    DateTime Time,
    double DownloadMbps,
    double UploadMbps,
    double? PingMs = null,
    double? DownloadLatencyMs = null,
    double? UploadLatencyMs = null);

/// <summary>Cheap snapshot for the live view tiles.</summary>
public record IspHealthSnapshot(IspHealthStatus Status, int? Score, DateTime? ComputedAt)
{
    /// <summary>Tile text: the score when ready, otherwise a setup/progress hint.</summary>
    public string TileText => Status switch
    {
        IspHealthStatus.Ready when Score.HasValue => Score.Value.ToString(),
        IspHealthStatus.Computing or IspHealthStatus.InsufficientData => "...",
        _ => "Set up"
    };

    /// <summary>Tile tooltip explaining the state; the tile always links to the tab.</summary>
    public string TileTooltip => Status switch
    {
        IspHealthStatus.Ready when Score.HasValue => $"ISP Health: {IspHealthReport.GradeLabel(Score.Value)}",
        IspHealthStatus.Computing => "Analyzing recent ISP data",
        IspHealthStatus.InsufficientData => "Collecting data - ISP Health needs a few hours of monitoring",
        IspHealthStatus.NeedsDiscovery => "Run Upstream Discovery to enable ISP Health",
        IspHealthStatus.NeedsTechnology => "Select your access technology to enable ISP Health",
        _ => "Set up monitoring to enable ISP Health"
    };

    /// <summary>Color class for the tile score text, matching the gauge thresholds.</summary>
    public string TileCssClass => Status != IspHealthStatus.Ready || !Score.HasValue
        ? "isp-score-none"
        : Score.Value switch
        {
            >= 90 => "isp-score-excellent",
            >= 75 => "isp-score-good",
            >= 60 => "isp-score-fair",
            _ => "isp-score-poor"
        };
}
