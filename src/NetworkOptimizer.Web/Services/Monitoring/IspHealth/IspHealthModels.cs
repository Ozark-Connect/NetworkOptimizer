using NetworkOptimizer.Storage.Models;

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
    /// <summary>The site's console connection is not up yet, so the expected ISP plan speeds
    /// can't be read; the compute is deferred until it connects.</summary>
    AwaitingConnection,
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

    /// <summary>
    /// For transit ASNs (Arm 4): the involvement-weight tooltip, e.g. "Carries 6 of 8 monitored
    /// internet hosts - weighted at 100% in Transit Health". Null when involvement doesn't
    /// differentiate (no host attributable to any transit, so all fall to equal weight) - the UI
    /// shows the fraction icon only when this is set.
    /// </summary>
    public string? InvolvementTooltip { get; init; }

    /// <summary>For a reach-0 transit ASN scoring below 80: the score-text caveat about ICMP
    /// deprioritization, copied from <see cref="IspAsnHealth.LowReachScoreCaveat"/>. Null otherwise.</summary>
    public string? LowReachScoreCaveat { get; init; }
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
    public double? ScoredJitterMs { get; init; }

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

    /// <summary>
    /// Arm 4 - internet-host involvement, for transit ASNs. Set only when attribution is known
    /// (hop order + destinations); left false/null for ISP ASNs and unattributable installs.
    /// </summary>
    public bool ShowInvolvement { get; set; }

    /// <summary>How many monitored internet destinations are proven to route through this ASN.</summary>
    public int InvolvementReach { get; set; }

    /// <summary>Total monitored internet destinations (the denominator of the involvement fraction).</summary>
    public int InvolvementHostTotal { get; set; }

    /// <summary>
    /// The 0.25-1.0 weight this ASN's own score carries in the involvement-weighted Transit Health
    /// average. Floored at 0.25 so an off-path transit still counts - it may be on your return path or a
    /// failover route - but only lightly. Null when involvement isn't shown.
    /// </summary>
    public double? InvolvementWeight { get; set; }

    /// <summary>Fraction-icon tooltip built from the involvement fields; null when not shown.</summary>
    public string? InvolvementTooltip => !ShowInvolvement || InvolvementWeight is not double w ? null
        : InvolvementReach > 0
            ? $"Carries {InvolvementReach} of {InvolvementHostTotal} internet targets (forward path), {w * 100:0}% weight"
            : $"Off the forward path; held at {w * 100:0}% (likely the return path from popular services)";

    /// <summary>Caveat for the score TEXT of a reach-0 transit ASN scoring below 80: nothing monitored
    /// routes through it on the forward path, so a depressed score may be ICMP deprioritization at its
    /// routers rather than real path trouble. Only for real transit ASNs (AsnNumber &gt; 0) with known
    /// attribution and zero forward-path reach; null otherwise.</summary>
    public string? LowReachScoreCaveat => AsnNumber > 0 && ShowInvolvement && InvolvementReach == 0
        && OverallScore is int s && s < 80
        ? "Lightly weighted - nothing we are monitoring routes through this network; a low score is likely just ICMP deprioritization."
        : null;

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

    /// <summary>
    /// The hop's address and PTR name, ready to display: "192.0.2.1 - core1.example.net", or just the
    /// address when it has no PTR. Hops are labelled by ASN name (e.g. "PenTeleData 1"), which is
    /// readable but tells you nothing about WHICH router answered - so an operator comparing our
    /// reading against their own traceroute had no way to line the two up (#1070). Null when the
    /// target has since been removed.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>Displayed RTT: winsorized mean over the window.</summary>
    public double? RttMs { get; init; }

    /// <summary>Effective (absolved) jitter this hop is graded on.</summary>
    public double? ScoredJitterMs { get; init; }

    /// <summary>This hop's own measured jitter, before any absolve.</summary>
    public double? RawJitterMs { get; init; }

    /// <summary>True when a cleaner witness (transit/sibling/destination) pulled this hop's jitter below its own reading.</summary>
    public bool JitterAssimilated { get; init; }

    public double? LossPct { get; init; }

    /// <summary>
    /// 0-100 for how steady this hop's round-trip time stayed across the window, from its RTT median
    /// absolute deviation. Distinct from jitter, which measures variation between probes: a hop can be
    /// jittery per-probe yet steady across the day, or smooth per-probe while drifting badly. Taken
    /// from the hop's grade so the row and the grade beside it cannot disagree; null when ungraded.
    /// </summary>
    public int? LatencyStabilityScore { get; init; }

    /// <summary>
    /// 0-100 for how little of the window this hop spent congested. Score-only, like stability: the
    /// card shows jitter and loss as measurements, so these two are the components of the grade that
    /// have no other representation on screen.
    /// </summary>
    public int? CongestionScore { get; init; }

    /// <summary>Confirmed congestion events attributed to this hop, shown in place of the score.</summary>
    public int CongestionEventCount { get; init; }

    /// <summary>Per-hop quality grade (stability, jitter, loss, congestion, intra-ASN reach).</summary>
    public int? OverallScore { get; init; }

    /// <summary>RTT beyond this ASN's nearest hop (0 for the nearest). Drives the soft reach ceiling.</summary>
    public double? ReachDeltaMs { get; init; }

    /// <summary>True for the first clean hop, the target the access layer idle latency comes from.</summary>
    public bool IsGradedHop { get; init; }

    /// <summary>
    /// True when this hop answers pings but appears in no discovery trace (HopNumber 0) - e.g. an
    /// OLT/CMTS that ICMP-deprioritizes traceroute. Its jitter isn't a forward-path signal.
    /// </summary>
    public bool NotOnTracedPath { get; init; }

    /// <summary>
    /// True when this hop is off the traced path AND its jitter is materially degrading its score -
    /// a strong "you probably want to stop scoring this" signal that drives the ISP Health disable hint.
    /// </summary>
    public bool SuggestDisable { get; init; }
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

    /// <summary>Optional deep-link to the matching loss event on the Network Performance charts,
    /// mirroring the Access Layer sub-score headers (e.g. ?investigate=loaded-loss).</summary>
    public string? InvestigateUrl { get; init; }
    public string? InvestigateText { get; init; }

    /// <summary>
    /// Onset times (UTC) of the outage events this finding aggregates, so the panel can offer
    /// the per-event "that was me" exclusion on the finding itself. Empty for non-outage findings.
    /// </summary>
    public List<DateTime> OutageStarts { get; init; } = new();
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
    // Settable (not init) so a long-window report can snap these to fine-resolution boundaries
    // after detection - see IspHealthService.RefineCongestionBoundariesAsync.
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

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

    /// <summary>
    /// True for a transit-unreachable event: the target(s) went to total loss for minutes -
    /// a route withdrawal/BGP change, not an RTT step. <see cref="Time"/> is the window
    /// start and <see cref="UnreachableEnd"/> its end; the median/delta fields are unused
    /// (0). The window's loss is carved out of the access-layer loss pool; the ASN's own
    /// network grade still sees it.
    /// </summary>
    public bool IsUnreachable { get; init; }

    /// <summary>End of the unreachable window (last dark sample); null for RTT-step shifts.</summary>
    public DateTime? UnreachableEnd { get; init; }
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
/// per-tier recovery shape are presentation only. Spans below
/// <see cref="IspHealthOptions.OutageBriefMaxSeconds"/> are flagged <see cref="IsBrief"/> -
/// short transit/upstream flaps surfaced on the timeline with only a fraction-of-a-point score hit.
/// </summary>
public class OutageEvent
{
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// True when this span is a brief disruption (shorter than
    /// <see cref="IspHealthOptions.OutageBriefMaxSeconds"/>) rather than a full outage. Brief
    /// disruptions are reported distinctly and ride the low end of the severity curve, so their
    /// score impact is at most a point. Set by the detector.
    /// </summary>
    public bool IsBrief { get; init; }

    /// <summary>
    /// True when this is a partial-loss disruption (degradation): a coincident burst of elevated
    /// loss across many path targets that never reached the near-total dark threshold, so nothing
    /// went fully dark. Distinct from a blackout outage - reported by peak loss and breadth rather
    /// than a dark/recovery waterfall, and its loss is NOT masked from the Packet Loss factor.
    /// </summary>
    public bool IsPartial { get; init; }

    /// <summary>
    /// For a partial event: the loss reached the blackout dark threshold on (nearly) every
    /// reporting hop - a short total outage whose bucket-edge dilution kept it out of the
    /// blackout pass. Presentation upgrades the wording to total loss; scoring is unchanged.
    /// </summary>
    public bool IsNearTotal { get; init; }

    /// <summary>Worst per-target loss reached during the event. Feeds the partial-loss summary and the
    /// scorer's severity depth (peak loss fraction). Populated for blackouts and partials alike.</summary>
    public double PeakLossPct { get; init; }

    /// <summary>Targets that dropped during the event (dark for a blackout, degraded for a partial) -
    /// the numerator of the severity breadth fraction.</summary>
    public int DegradedTargetCount { get; init; }

    /// <summary>Total targets reporting during the event, the denominator for the breadth fraction.</summary>
    public int PathTargetCount { get; init; }

    /// <summary>Whether even the access hop went dark, or it held while everything beyond it dropped.</summary>
    public OutageScope Scope { get; init; }

    /// <summary>
    /// The deepest tier/hop that stayed reachable through the outage - the break sat just
    /// beyond it. Only hops with a known trace-map position anchor this (ASN label preferred
    /// over the per-target name). Null when even the access hop went dark (a full WAN outage)
    /// or when no anchorable hop stayed clean - see <see cref="BrokenNetwork"/>.
    /// </summary>
    public string? LastReachableHop { get; init; }

    /// <summary>
    /// For an Upstream outage that no trace-map-anchored hop can localize (only off-map rows
    /// stayed reachable): the ASN of the network the break surfaced in - the shallowest dark
    /// hop's ASN label. Null when <see cref="LastReachableHop"/> is set or no dark ASN is known.
    /// </summary>
    public string? BrokenNetwork { get; init; }

    /// <summary>Per-tier loss and recovery, nearest first, for the outage-shape display.</summary>
    public List<OutageTierState> Tiers { get; init; } = new();

    /// <summary>
    /// Points this outage contributed to the ISP Health penalty - its duration share of the
    /// total (curve-based) outage penalty. 0 for Local (LAN/gateway) outages, which are never
    /// scored. Set by the scorer so each outage row can show its own "-N points".
    /// </summary>
    public int ScorePenaltyPoints { get; set; }

    /// <summary>
    /// Time-of-day usage weight 0..1: how heavily the line is typically in use during this event's
    /// local hours, relative to its busiest hour, floored by <see cref="IspHealthOptions.UsageWeightFloor"/>.
    /// 1.0 when usage weighting is off or the fingerprint is unavailable. Scales the outage's score
    /// penalty so a drop during a quiet hour dings less than the same drop at peak. Set by the service.
    /// </summary>
    public double UsageWeight { get; set; } = 1.0;

    /// <summary>
    /// True when the user marked this event "that was me" (their own maintenance - pulled the
    /// coax to add a pad, re-seated a connector), excluding it from the outage score penalty
    /// and the findings. Still shown on the timeline, and a blackout's dark window still
    /// masks its loss from the other factors. Set by the service from the persisted
    /// acknowledgements, matched to the onset time by tolerance.
    /// </summary>
    public bool Acknowledged { get; set; }
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

    /// <summary>
    /// Percent of the window the internet was reachable, 0..100. A blackout counts for its full
    /// duration; a partial-loss disruption counts for the share of it that was actually lost, since
    /// the detector only declares one past a broad multi-ASN half-loss (an interruption, not a blip)
    /// but half the packets is not half the clock. Local (LAN/gateway) and acknowledged outages are
    /// excluded, same as from the score penalty. Deliberately NOT usage-weighted, unlike the penalty:
    /// how much an outage mattered is a scoring judgment, while uptime is a fact about the line -
    /// so this figure and the scored unavailability can differ, by design. 100 when nothing dropped.
    /// </summary>
    public double UptimePercent { get; init; } = 100;

    /// <summary>Total downtime behind <see cref="UptimePercent"/>; zero when nothing dropped.</summary>
    public TimeSpan Downtime { get; init; }

    /// <summary>
    /// Targets left out of the Packet Loss and Loaded Loss pool because they were dark for the whole
    /// window while their peers kept measuring (see <see cref="LossPoolFilter"/>). Investigate reads
    /// this so its pooled figure grades the same set the score did.
    /// </summary>
    public IReadOnlyCollection<string> LossPoolExcludedTargetIds { get; init; } = Array.Empty<string>();

    public required AccessProfile Profile { get; init; }

    /// <summary>The access technology that selected <see cref="Profile"/>, so the UI can offer a
    /// quick re-score by changing it (the pill selector) without round-tripping Upstream Discovery.</summary>
    public AccessTechnology AccessTechnology { get; set; }

    /// <summary>
    /// True when the WAN carries its traffic over a PPPoE session, detected from the gateway's
    /// data-path interface rather than chosen by the user, and <see cref="Profile"/> therefore
    /// carries the PPPoE overlay (IspHealthProfiles.ApplyPppoeSession). Surfaced so the UI can
    /// say what the line is being graded as - the overlay shifts the thresholds materially, and
    /// a user who sees only "GPON" has no way to tell it was accounted for.
    /// </summary>
    public bool PppoeSession { get; set; }

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

    /// <summary>
    /// Physical-link source candidates for the WAN. More than one means the match was
    /// ambiguous; the panel renders a picker. Set by the service after scoring (display only).
    /// </summary>
    public List<PhysicalLinkCandidate> PhysicalLinkCandidates { get; set; } = new();

    /// <summary>The currently selected physical-link source key (e.g. "cm:3"), if any.</summary>
    public string? PhysicalLinkSelectedKey { get; set; }

    /// <summary>The access medium of the selected physical-link source, for display verbiage
    /// (e.g. a PON SFP reads as an ONT, an Active Ethernet SFP as a transceiver). Null when no
    /// single source matched.</summary>
    public PhysicalMedium? PhysicalLinkMedium { get; set; }

    /// <summary>True when multiple sources matched and the user has not yet picked one.</summary>
    public bool PhysicalLinkAmbiguous { get; set; }

    /// <summary>
    /// Which WAN this report scored, from the console's primary-WAN resolution. Display only,
    /// and set by the service after scoring. ISP Health grades one WAN today; naming it means
    /// a report stays unambiguous once multi-WAN scoring arrives, and an exported one is still
    /// readable a month later. Null when the console did not report a WAN.
    /// </summary>
    public string? WanName { get; set; }

    /// <summary>The WAN's console network group ("WAN", "WAN2"), which is what distinguishes
    /// one WAN from another when both carry the same provider name.</summary>
    public string? WanNetworkGroup { get; set; }

    /// <summary>The OS interface backing the WAN (e.g. "eth4"), when the console reports it.</summary>
    public string? WanInterface { get; set; }

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

/// <summary>
/// Load classification of one aggregate window. The Classified* pair is the rates' verdict before
/// isolated runs were demoted, read only as the opposite-direction barrier when matching samples to
/// load: demotion clears the live flag on a speed test's shorter phase, and with it the marker that
/// keeps the surviving phase from reaching across the handover.
/// </summary>
public record LoadWindow(
    bool IsIdle,
    bool IsLoadedDown,
    bool IsLoadedUp,
    bool ClassifiedLoadedDown = false,
    bool ClassifiedLoadedUp = false);

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

    /// <summary>Monitored address per target id, so the ISP Network breakout can name the hop it read.</summary>
    public IReadOnlyDictionary<string, string> TargetAddresses { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Per-target series pooled for packet loss (ISP + transit + anycast DNS targets).</summary>
    public List<List<LatencySample>> LossPoolSeries { get; init; } = new();

    /// <summary>
    /// Targets dropped from <see cref="LossPoolSeries"/> as flat-lined (see
    /// <see cref="LossPoolFilter"/>). Carried through so the report can name them: the pool itself is
    /// anonymous, and Investigate has to grade the same set the score did.
    /// </summary>
    public IReadOnlyCollection<string> LossPoolExcludedTargetIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The LAN gateway's own series, when a gateway target is monitored. Its LOSS becomes the floor
    /// under every upstream loss measurement (see <see cref="GatewayLossFloor"/>); its latency is
    /// deliberately not used that way. Empty when no gateway target exists, which leaves every
    /// measurement untouched.
    /// </summary>
    public List<LatencySample> GatewayLossSeries { get; init; } = new();

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

    /// <summary>
    /// Witness-only series (user-added Custom targets, e.g. a known-stable CMTS/PoP ping). They join
    /// the routes-through absolution witness pool alongside <see cref="DestinationSeries"/>, but are
    /// deliberately NOT treated as internet destinations - they don't count toward transit
    /// involvement and can't be selected as IX-peering-reached destinations (a nearby CMTS is
    /// low-delta and crosses no transit, so it would otherwise be mistaken for a peered endpoint).
    /// </summary>
    public List<AsnSeries> WitnessSeries { get; init; } = new();

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

    /// <summary>Whether OUR Adaptive SQM is enabled and configured for the primary WAN. Distinct
    /// from <see cref="SmartQueuesEnabled"/> (UniFi's base feature); used so the loaded-loss
    /// recommendation never pitches Adaptive SQM to someone already running it.</summary>
    public bool AdaptiveSqmEnabled { get; init; }

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

    /// <summary>
    /// TargetIds of monitored hops that answer pings but appear in no discovery trace (HopNumber 0) -
    /// used to flag likely-disable ISP hops whose jitter isn't a forward-path signal.
    /// </summary>
    public IReadOnlySet<string> NotTracedTargetIds { get; init; } = new HashSet<string>();

    /// <summary>
    /// Window-aggregated physical-link metrics for the one source matched to the WAN, or null
    /// when no source matched (or the match was ambiguous and unresolved). Drives the Access
    /// Layer Physical Link factor.
    /// </summary>
    public PhysicalLinkInput? PhysicalLink { get; init; }
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
        IspHealthStatus.Computing or IspHealthStatus.InsufficientData or IspHealthStatus.AwaitingConnection => "...",
        _ => "Set up"
    };

    /// <summary>Tile tooltip explaining the state; the tile always links to the tab.</summary>
    public string TileTooltip => Status switch
    {
        IspHealthStatus.Ready when Score.HasValue => $"ISP Health: {IspHealthReport.GradeLabel(Score.Value)}",
        IspHealthStatus.Computing => "Analyzing recent ISP data",
        IspHealthStatus.AwaitingConnection => "Waiting for the console connection to read your ISP plan speeds",
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
