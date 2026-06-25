using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// All ISP Health tunables in one place: score weights, load classification
/// thresholds, congestion and step-change detector parameters, and the SQM
/// recommendation rule. Defaults are provisional and tuned against real incident data.
/// </summary>
public class IspHealthOptions
{
    /// <summary>Trailing analysis window in hours.</summary>
    public int ScoreWindowHours { get; set; } = 48;

    /// <summary>Minimum hours of latency data required before a score is shown (new installs).</summary>
    public int MinDataHours { get; set; } = 4;

    /// <summary>Weight of the access-layer dimension in the overall score.</summary>
    public double AccessWeight { get; set; } = 1.0 / 3.0;

    /// <summary>Weight of the transit-health dimension in the overall score.</summary>
    public double TransitWeight { get; set; } = 1.0 / 3.0;

    /// <summary>Weight of the ISP-ASN-health dimension in the overall score.</summary>
    public double IspAsnWeight { get; set; } = 1.0 / 3.0;

    /// <summary>Weight of WAN speed test throughput vs the configured plan within the access dimension.</summary>
    public double SpeedVsPlanWeight { get; set; } = 0.30;

    /// <summary>Weight of idle latency within the access dimension.</summary>
    public double IdleLatencyWeight { get; set; } = 0.10;

    /// <summary>
    /// Weight of packet loss within the access dimension. Loss is the heaviest signal
    /// after speed: it captures both steady physical-layer loss and (via the capped
    /// outage penalty) internet-unreachable outages, which users care about most.
    /// </summary>
    public double IdleLossWeight { get; set; } = 0.25;

    /// <summary>Weight of loaded latency delta within the access dimension.</summary>
    public double LoadedLatencyWeight { get; set; } = 0.175;

    /// <summary>Weight of loaded packet loss within the access dimension.</summary>
    public double LoadedLossWeight { get; set; } = 0.175;

    /// <summary>How far back to fall when no WAN speed test ran inside the score window.</summary>
    public int SpeedTestFallbackDays { get; set; } = 7;

    /// <summary>Fraction of the lowest WAN speed test results discarded as outliers (broken servers, flukes).</summary>
    public double SpeedTestOutlierTrimFraction { get; set; } = 0.15;

    /// <summary>Weight of the best post-trim result (demonstrated capacity) in the speed factor.</summary>
    public double SpeedCapacityWeight { get; set; } = 0.6;

    /// <summary>Weight of the median post-trim result (typical delivery) in the speed factor.</summary>
    public double SpeedTypicalWeight { get; set; } = 0.4;

    /// <summary>
    /// Targets within this many ms of an ASN's nearest hop form its first POP/handoff
    /// cluster; only that cluster is graded, so monitoring far hops (e.g. a distant IX)
    /// does not inflate the ASN's latency, jitter, or reach scores.
    /// </summary>
    public double AsnHopClusterToleranceMs { get; set; } = 2.0;

    /// <summary>Fraction of expected speed at or above which a sample counts as loaded.</summary>
    public double LoadedThresholdFraction { get; set; } = 0.50;

    /// <summary>Fraction of expected speed below which (both directions) a sample counts as idle.</summary>
    public double IdleThresholdFraction { get; set; } = 0.30;

    /// <summary>Minimum loaded samples per direction for the loaded factors to score that direction.</summary>
    public int MinLoadedSamples { get; set; } = 5;

    /// <summary>Aggregate window in minutes for chart series.</summary>
    public int AggregateWindowMinutes { get; set; } = 1;

    /// <summary>
    /// Window in seconds for load classification and the latency/throughput join.
    /// Solid traffic across one such window counts as loaded evidence, so short
    /// bursts (speed tests, large downloads) register instead of diluting into
    /// minute-level means.
    /// </summary>
    public int LoadWindowSeconds { get; set; } = 7;

    /// <summary>
    /// Seconds the loaded-window leading edge is extended backward when matching loaded latency
    /// and loaded loss samples. A load event has a ramp (queue fills before throughput crosses the
    /// loaded threshold) whose early latency/loss falls in transition windows that are neither idle
    /// nor loaded and would otherwise be dropped. Dilating the leading edge reclaims it. Bucket-
    /// quantized to <see cref="LoadWindowSeconds"/>, so ~5 s extends by one window.
    /// </summary>
    public int LoadedLeadSeconds { get; set; } = 5;

    /// <summary>
    /// Seconds the loaded-window trailing edge is extended forward when matching loaded latency and
    /// loaded loss samples. Loss especially concentrates at the saturation tail / buffer drain and is
    /// end-stamped at probe-burst completion, so it lands a few seconds after the WAN rate window that
    /// defines the load (observed ~5 s after a GPON speed test's download phase ended). Dilation
    /// captures it without the leading-edge loss a fixed back-shift causes. Dilation never crosses into
    /// an opposite-direction loaded run, so a speed test's download tail cannot bleed into its upload
    /// phase. Bucket-quantized to <see cref="LoadWindowSeconds"/>.
    /// </summary>
    public int LoadedTailSeconds { get; set; } = 5;

    /// <summary>
    /// How long a computed report stays fresh before the ISP Health tab recomputes it.
    /// The score is a 48 h rolling metric, so a few minutes of staleness is invisible; the
    /// "Last updated" label discloses the age. Each recompute is a heavy Influx query burst,
    /// so this is kept generous.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Max report age the glanceable dashboard / Live View score tile tolerates before it
    /// triggers a background recompute. Longer than <see cref="CacheTtl"/> because the tile is
    /// a summary, not the detail view - this decouples the tile from the recompute cadence so
    /// simply watching Live View doesn't drive a full ISP Health query every few minutes.
    /// </summary>
    public TimeSpan DashboardScoreTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Bucket size in minutes for congestion detection.</summary>
    public int CongestionBucketMinutes { get; set; } = 15;

    /// <summary>Minimum sustained duration in minutes for a congestion event.</summary>
    public int CongestionMinDurationMinutes { get; set; } = 30;

    /// <summary>Bucket RTT must exceed baseline by this many MADs (or the absolute floor) to be elevated.</summary>
    public double CongestionRttDeltaFactor { get; set; } = 3.0;

    /// <summary>
    /// Absolute floor in ms for the congestion RTT elevation threshold. Tuned against
    /// a real shared congestion event (+3-7 ms on 8-16 ms paths); 5.0 caught only the
    /// peak 30 minutes of a 105-minute event.
    /// </summary>
    public double CongestionRttMinDeltaMs { get; set; } = 2.0;

    /// <summary>Bucket jitter must exceed baseline jitter by this factor to be elevated.</summary>
    public double CongestionJitterFactor { get; set; } = 2.0;

    /// <summary>
    /// Burst criterion: a bucket is also elevated when its p90 RTT exceeds the
    /// baseline p90 by this factor times the baseline p90-median spread. Catches
    /// intermittent-spike congestion that leaves bucket medians untouched
    /// (validated against a real 4 h bursty access-hop event).
    /// </summary>
    public double CongestionBurstDeltaFactor { get; set; } = 1.5;

    /// <summary>
    /// Score points deducted from an ASN's congestion factor per hour of congestion
    /// events. At 20 a 30-min event costs 10, a 1 h event 20, a 2.5 h event zeros the
    /// factor; combined with the congestion weight this is a real, visible downgrade.
    /// </summary>
    public double CongestionPenaltyPerHour { get; set; } = 20.0;

    /// <summary>Minimum number of ASNs with overlapping events to merge them into a shared upstream event.</summary>
    public int SharedEventMinAsns { get; set; } = 2;

    /// <summary>Minimum time overlap (fraction of the shorter event) for events to be considered simultaneous.</summary>
    public double SharedEventOverlapFraction { get; set; } = 0.5;

    /// <summary>
    /// Absolute floor in ms a bucket's jitter delta must clear, on top of the
    /// <see cref="CongestionJitterFactor"/> ratio gate, to count as elevated. An
    /// ultra-stable far hop with a near-zero baseline (e.g. a 0.26 ms transit hop) trips
    /// the multiplicative gate on a fraction of a millisecond of shared return-path wobble;
    /// this stops that. Sustained genuine congestion clears it easily.
    /// </summary>
    public double CongestionJitterMinDeltaMs { get; set; } = 0.8;

    /// <summary>
    /// Hysteresis for congestion run continuation. Starting a run still requires the full entry
    /// gate (RTT+jitter, or the burst shape); but once a run is active it stays alive while any
    /// signal remains at least this fraction of the way from baseline to its entry threshold.
    /// Without it a long event whose tail hovers just under the entry gate (the milder second half
    /// of a real multi-hour event) flickers in and out and truncates to its peak. Entry is
    /// unchanged, so this only extends events that already legitimately started - never creates new
    /// ones.
    /// </summary>
    public double CongestionSustainFraction { get; set; } = 0.5;

    /// <summary>
    /// A localized congestion event reports the shared time-cluster window when its own bottleneck's
    /// elevation covers at least this fraction of that window; a member substantially shorter (an
    /// outlier that cleared early while co-occurring hops lingered) reports its own span instead.
    /// Keeps a genuine co-temporal shared event presented as one clean window, while still breaking
    /// out a hop whose duration is very different. At 1.0 every member uses its own exact span; at 0
    /// every member uses the full cluster window.
    /// </summary>
    public double CongestionSharedWindowMinFraction { get; set; } = 0.7;

    /// <summary>
    /// WAN utilization (fraction of the expected plan speed, worst direction) at or above
    /// which a congestion bucket is treated as coinciding with heavy local load. The
    /// self-inflicted classifier uses this to tell access-egress bufferbloat (your own
    /// traffic saturating your access link) from external path congestion. Requires known
    /// expected speeds; without them load-coincidence is left undetermined and the event is
    /// reported, never suppressed.
    /// </summary>
    public double CongestionLoadHighFraction { get; set; } = 0.5;

    /// <summary>
    /// Fraction of an event's buckets that must coincide with heavy WAN load before the
    /// event is considered load-driven (the input to self-inflicted classification).
    /// </summary>
    public double CongestionLoadCoincidenceFraction { get; set; } = 0.5;

    /// <summary>
    /// Fraction of a hop's in-window RTT samples that must exceed its own baseline p90 (by the
    /// congestion RTT floor) for the localizer to count it as elevated when testing propagation.
    /// A real bottleneck's delay reaches downstream hops as excursions that are often too sparse
    /// to fire their own sustained congestion event; without this softer test the localizer would
    /// wrongly absolve a genuine bottleneck as control-plane noise because nothing downstream
    /// "fired". Clean off-path hops sit near zero, so a low bar separates them cleanly.
    /// </summary>
    public double CongestionPropagationExcursionFraction { get; set; } = 0.05;

    /// <summary>
    /// Factor a hop's in-window median jitter must clear over its own baseline median jitter to count
    /// as elevated for PROPAGATION. Many congestion incidents are jitter-driven with flat RTT, so an
    /// RTT-only propagation test reads the downstream as "clean" and wrongly absolves a real chain-wide
    /// rise as per-hop control-plane noise. Softer than the detection jitter factor; paired with an
    /// absolute floor below so a near-zero-baseline hop is not tripped by a sub-ms ratio swing.
    /// </summary>
    public double CongestionPropagationJitterFactor { get; set; } = 1.5;

    /// <summary>
    /// Minimum absolute median-jitter rise (ms) for the propagation jitter test, on top of the
    /// <see cref="CongestionPropagationJitterFactor"/> ratio - a 0.05 -> 0.10 ms doubling is still
    /// noise, not propagation.
    /// </summary>
    public double CongestionPropagationJitterFloorMs { get; set; } = 0.2;

    /// <summary>
    /// Loaded-latency uniformity gate. Under heavy WAN load every path picks up a shared FLOOR of
    /// added delay (that floor alone is loaded latency). A shared/loaded-latency event collapses to a
    /// single row only when the rise is UNIFORM: the worst path's RTT rise over its own baseline is
    /// within this factor of the median path's rise. If a few paths rose materially further than the
    /// floor (high variance), those are localized congestion ON TOP of the load - the event stays
    /// per-hop so the localizer surfaces them as "this hop's own capacity", not "everything slowed".
    /// </summary>
    public double CongestionLoadedUniformityFactor { get; set; } = 2.0;

    /// <summary>
    /// Clean-control floor multiple. A parallel path counts as a CLEAN control (proof a hop's
    /// elevation is its own capacity, not access bufferbloat) when its RTT rose no more than this
    /// multiple of the shared floor - i.e. it only picked up the load floor, not localized congestion.
    /// Must be tighter than <see cref="CongestionLoadedUniformityFactor"/> so the materially-worse
    /// hops are NOT counted as clean. Uses RTT (not jitter): under load the jitter floor lifts every
    /// path, so a jitter-inclusive "elevated" test would erase every clean control.
    /// </summary>
    public double CongestionCleanControlFloorFactor { get; set; } = 1.5;

    /// <summary>
    /// Self-inflicted access bufferbloat is a line-wide rise under load: this fraction of monitored
    /// paths (with data) must have their in-window median rise above baseline for an access-egress
    /// bottleneck under load to read as self-inflicted rather than a hop bottleneck. A robust majority
    /// (not "any clean path") so a lone high-variance path reading clean against the absolute elevation
    /// bar can't veto the call, while a genuinely mixed window (real clean controls) still blocks it.
    /// </summary>
    public double CongestionLineWideRiseFraction { get; set; } = 0.8;

    /// <summary>
    /// Minimum in-window median rise over baseline (ms) for a path to count toward the line-wide
    /// self-infliction test. Small, so the near-constant bufferbloat offset still registers on
    /// high-baseline hops where it sits under the absolute elevation floor.
    /// </summary>
    public double CongestionLineWideMinShiftMs { get; set; } = 0.5;

    /// <summary>
    /// In-window percentile used by the line-wide rise test. A high percentile (vs the median) keeps a
    /// path that rose strongly for a good part of the window counted as "rose" even when a long mild
    /// tail dilutes its median toward baseline - otherwise the line-wide breadth flickers across
    /// recomputes for an event with a strong core and a long tail. A flat path still sits at baseline.
    /// </summary>
    public double CongestionLineWideRisePercentile { get; set; } = 0.75;

    /// <summary>Window size in minutes for step-change median comparison.</summary>
    public int StepWindowMinutes { get; set; } = 30;

    /// <summary>Absolute floor in ms for a step-change candidate delta.</summary>
    public double StepMinDeltaMs { get; set; } = 1.2;

    /// <summary>Relative floor (fraction of the before-median) for a step-change candidate delta.</summary>
    public double StepMinRelativeChange { get; set; } = 0.06;

    /// <summary>Number of subsequent windows that must hold the new level to confirm a step.</summary>
    public int StepPersistenceWindows { get; set; } = 3;

    /// <summary>Absolute floor in ms for the window-stability gate (IQR width).</summary>
    public double StepStableIqrFloorMs { get; set; } = 1.5;

    /// <summary>
    /// Maximum IQR width as a fraction of the window median for a window to count as a
    /// stable level. Congested windows are noisy (wide IQR) and must not anchor a step;
    /// without this gate the trailing edge of a long congestion event reads as a step
    /// down (seen in real data).
    /// </summary>
    public double StepStableIqrFraction { get; set; } = 0.15;

    /// <summary>
    /// Bucket size in seconds for outage detection. Sub-minute so a brief (~30 s) drop resolves
    /// into its own fully-dark buckets instead of being diluted to a partial-loss bucket inside a
    /// one-minute window and failing the dark-fraction gate. At the internet targets' ~7-10 s
    /// effective sample cadence a 15 s bucket still holds one or two samples per target.
    /// </summary>
    public int OutageBucketSeconds { get; set; } = 15;

    /// <summary>
    /// Mean loss (percent) at or above which a tier counts as dark in an outage bucket.
    /// Near-total loss, not the lower congestion thresholds: an outage is the internet
    /// going unreachable, not slow.
    /// </summary>
    public double OutageDarkLossPct { get; set; } = 95.0;

    /// <summary>
    /// Fraction of the internet/destination targets that must be dark in a bucket for it
    /// to count as an outage bucket. A strong majority, so one dead probe target or a
    /// single unreachable CDN does not read as an internet outage.
    /// </summary>
    public double OutageCoverageFraction { get; set; } = 0.75;

    /// <summary>
    /// Minimum internet/destination targets that must be reporting (have a sample) in a
    /// bucket before an outage can be declared. Below this the evidence is too thin, and
    /// a bucket with no samples at all is a monitoring gap (console offline), never an outage.
    /// </summary>
    public int OutageMinReportingTargets { get; set; } = 2;

    /// <summary>
    /// Minimum sustained duration in seconds before a near-total-loss span is reported at all.
    /// Set above a momentary blip (a single lost probe group) but well below the brief/full
    /// divider, so a clean 30 s transit drop is captured rather than discarded.
    /// </summary>
    public int OutageMinDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Spans shorter than this are classified as brief disruptions (short transit/upstream flaps);
    /// at or above it they are full outages. This is the prior two-minute outage threshold, kept as
    /// the divider so what users already understood as an outage is unchanged - brief disruptions are
    /// a new, lighter tier beneath it that rides the low end of the severity curve. See
    /// <see cref="OutageEvent.IsBrief"/>.
    /// </summary>
    public int OutageBriefMaxSeconds { get; set; } = 120;

    /// <summary>
    /// Two outage runs separated by a healthy gap no longer than this are coalesced into one
    /// event. One real outage briefly clears the dark-fraction gate during staggered onset or
    /// inside-out recovery (targets dark/heal at slightly different times); without this it
    /// would fragment into several adjacent events. The sealed gap counts as downtime, so keep
    /// it short - the over-count is bounded by this value per seam.
    /// </summary>
    public int OutageMaxGapSeconds { get; set; } = 180;

    /// <summary>
    /// Bucket size in seconds for the partial-loss (degradation) pass. Wider than the blackout
    /// bucket because partial loss is route-specific: independent targets degrade at slightly
    /// different instants across the event, so a 15 s bucket holds only a couple at once. A 30 s
    /// window lets the coincident-but-staggered degradations land together for the breadth gate.
    /// </summary>
    public int OutagePartialBucketSeconds { get; set; } = 30;

    /// <summary>
    /// Loss (percent) at or above which a target counts as degraded for partial-loss detection.
    /// Below the near-total <see cref="OutageDarkLossPct"/> - a partial-loss burst is the path
    /// getting lossy, not unreachable.
    /// </summary>
    public double OutagePartialLossPct { get; set; } = 50.0;

    /// <summary>
    /// Minimum distinct path targets simultaneously degraded (within one partial bucket) for a
    /// partial-loss disruption. Coincident loss across many independent destinations is a real
    /// path event; one or two lossy targets are noise (ICMP rate-limiting, a single bad CDN node).
    /// </summary>
    public int OutagePartialMinTargets { get; set; } = 4;

    /// <summary>
    /// Minimum distinct ASNs/destinations spanned by the degraded targets, alongside
    /// <see cref="OutagePartialMinTargets"/>. Guards against several targets behind one ASN all
    /// degrading together (that ASN's own issue, not a path-wide event) tripping detection.
    /// </summary>
    public int OutagePartialMinAsns { get; set; } = 2;

    /// <summary>Minimum sustained duration in seconds for a partial-loss disruption.</summary>
    public int OutagePartialMinDurationSeconds { get; set; } = 20;

    /// <summary>
    /// Scales a partial-loss disruption's severity-curve penalty relative to a full outage of the
    /// same duration: the curve input is duration x (peak loss fraction) x this weight, so the ding
    /// stays tiny (a 30 s / 80% event is about a point). Partial loss also still feeds the Packet
    /// Loss factor (these events are deliberately not masked from it), so this is a small additional
    /// nudge for visibility, with the minor overlap accepted by design.
    /// </summary>
    public double OutagePartialPenaltyWeight { get; set; } = 1.0;

    /// <summary>
    /// Recovery-time tolerance for collapsing per-target access ISP rows in the outage waterfall:
    /// access hops that recovered within this many seconds of each other share a signature and
    /// merge to one row; ones outside it stay separate (the inside-out heal).
    /// </summary>
    public int OutageAccessGroupToleranceSeconds { get; set; } = 10;

    /// <summary>
    /// Outage severity curve: points deducted from the OVERALL score per (totalDowntimeMinutes,
    /// penaltyPoints) anchor, interpolated. Applied at the top level rather than buried in the
    /// Packet Loss factor (where the dimension weights would dilute a multi-hour outage to a
    /// couple of points). Scored by total duration alone - shape-independent. A brief blip barely
    /// registers; ~10 min is a clear ding; multi-hour drives the score toward zero.
    /// </summary>
    public (double Minutes, double Penalty)[] OutageSeverityCurve { get; set; } =
    {
        (0, 0), (5, 7), (10, 14), (30, 28), (60, 45), (180, 70), (480, 90)
    };

    /// <summary>Loaded delta beyond excellent by this many band-widths triggers the SQM recommendation.</summary>
    public double SqmDeviationFactor { get; set; } = 1.0;

    /// <summary>Congestion events in the window at or above which Adaptive SQM is suggested.</summary>
    public int SqmRecurringCongestionEvents { get; set; } = 2;

    /// <summary>Weight of latency stability (MAD/median) in the per-ASN quality blend.</summary>
    public double AsnLatencyStabilityWeight { get; set; } = 0.25;

    /// <summary>Weight of jitter in the per-ASN quality blend.</summary>
    public double AsnJitterWeight { get; set; } = 0.25;

    /// <summary>Weight of packet loss in the per-ASN quality blend.</summary>
    public double AsnLossWeight { get; set; } = 0.2;

    /// <summary>Weight of congestion in the per-ASN quality blend.</summary>
    public double AsnCongestionWeight { get; set; } = 0.3;

    /// <summary>
    /// Lower clamp for the path jitter floor used in floor-relative jitter scoring.
    /// Below this, ratio scoring would punish sub-millisecond jitter that is excellent
    /// in absolute terms, so the floor never reads below this value.
    /// </summary>
    public double JitterFloorMinMs { get; set; } = 0.3;

    /// <summary>
    /// Upper clamp for the path jitter floor. Above this the line is jittery
    /// everywhere; the absolute high-end anchors take over rather than letting a high
    /// floor excuse genuinely bad jitter.
    /// </summary>
    public double JitterFloorMaxMs { get; set; } = 1.5;

    /// <summary>
    /// Minimum amount a cleaner witness must sit below a hop's own jitter before it counts as
    /// assimilated. Within this band the difference is measurement noise, so the hop keeps its
    /// own jitter (no absolve, no assimilation icon). Applies to ISP hops and transit ASNs.
    /// </summary>
    public double JitterAssimilationMinDeltaMs { get; set; } = 0.05;

    /// <summary>
    /// Item D - how strongly the internet-relative reach ceiling absolves an ISP access hop's
    /// intra-ASN distance penalty. finalCeiling = C_intra + alpha * max(0, C_net - C_intra), so it
    /// only ever lifts (never lowers) and partially (alpha &lt; 1) - genuine distance/glass always
    /// leaves a mark, but a hop that's modest relative to where the internet actually sits isn't
    /// hammered for normal in-region distance on a geographically large access network.
    /// </summary>
    public double AccessReachInternetBlendAlpha { get; set; } = 0.40;

    /// <summary>
    /// Average WAN load at which packet loss reaches its worst on shared-medium access
    /// (DOCSIS, PON, fixed wireless). The load-calibrated loss ceiling reaches the
    /// connection's loaded-loss band at this utilization and holds there above it, rather
    /// than only at 100%.
    /// </summary>
    public double LossSaturationLoadFraction { get; set; } = 0.75;

    /// <summary>
    /// Upper percentile at which displayed RTT is winsorized: samples above it are capped
    /// to it before averaging, so a route flap or single bad probe can't distort the mean
    /// while sustained elevation still shows. 0.99 caps only the worst 1%.
    /// </summary>
    public double RttWinsorPercentile { get; set; } = 0.99;
}

/// <summary>
/// Per-access-technology expectations used to anchor the access-layer score curves.
/// Latency values are first-clean-ISP-hop RTTs in ms; loss values are percentages.
/// </summary>
public sealed record AccessProfile(
    string DisplayName,
    double IdleRttIdealMs,
    double IdleRttNormalLowMs,
    double IdleRttNormalHighMs,
    double IdleRttPoorMs,
    double IdleLossIdealPct,
    double IdleLossAcceptablePct,
    double LoadedLossDownLowPct,
    double LoadedLossDownHighPct,
    double LoadedLossUpLowPct,
    double LoadedLossUpHighPct,
    double LoadedDeltaExcellentMs,
    double LoadedDeltaAcceptableMs,
    bool IsNeutral = false,
    // Per-tech jitter band (P95 jitter, ms). When set, jitter is scored straight off the band -
    // (ideal,100) (typical,90) (poor,25) (2*poor,0) - so a medium's inherent jitter (e.g. DOCSIS's
    // ~3 ms request-grant) reads as normal instead of being graded against a sub-ms path floor.
    // Null (neutral / PPPoE / Other) keeps the measured-path-floor jitter curve. Applies path-wide
    // to ISP and transit jitter, since every probe crosses the access medium.
    double? JitterIdealMs = null,
    double? JitterTypicalMs = null,
    double? JitterPoorMs = null);

/// <summary>
/// Static catalog of access technology profiles. Returns null for
/// <see cref="AccessTechnology.Unknown"/>, which means ISP Health cannot score
/// and the UI funnels the user to Upstream Discovery. The Satellite profile
/// assumes LEO (Starlink-class); GEO satellite is future work.
/// </summary>
public static class IspHealthProfiles
{
    public static AccessProfile? GetProfile(AccessTechnology technology) => technology switch
    {
        AccessTechnology.Gpon => new AccessProfile("GPON",
            IdleRttIdealMs: 1.5, IdleRttNormalLowMs: 2.0, IdleRttNormalHighMs: 3.0, IdleRttPoorMs: 8.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.05,
            LoadedLossDownLowPct: 1.0, LoadedLossDownHighPct: 2.0,
            LoadedLossUpLowPct: 0.5, LoadedLossUpHighPct: 1.0,
            LoadedDeltaExcellentMs: 2.0, LoadedDeltaAcceptableMs: 10.0,
            JitterIdealMs: 0.4, JitterTypicalMs: 0.7, JitterPoorMs: 3.0),

        AccessTechnology.XgsPon => new AccessProfile("XGS-PON",
            IdleRttIdealMs: 1.5, IdleRttNormalLowMs: 2.0, IdleRttNormalHighMs: 3.0, IdleRttPoorMs: 8.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.05,
            LoadedLossDownLowPct: 0.5, LoadedLossDownHighPct: 1.0,
            LoadedLossUpLowPct: 0.25, LoadedLossUpHighPct: 0.5,
            LoadedDeltaExcellentMs: 2.0, LoadedDeltaAcceptableMs: 10.0,
            JitterIdealMs: 0.4, JitterTypicalMs: 0.7, JitterPoorMs: 3.0),

        AccessTechnology.Docsis => new AccessProfile("DOCSIS",
            IdleRttIdealMs: 7.0, IdleRttNormalLowMs: 9.0, IdleRttNormalHighMs: 12.0, IdleRttPoorMs: 25.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.2,
            LoadedLossDownLowPct: 3.0, LoadedLossDownHighPct: 5.0,
            LoadedLossUpLowPct: 3.0, LoadedLossUpHighPct: 5.0,
            LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 20.0,
            JitterIdealMs: 1.75, JitterTypicalMs: 3.0, JitterPoorMs: 6.0),

        AccessTechnology.Satellite => new AccessProfile("Satellite (LEO)",
            IdleRttIdealMs: 23.0, IdleRttNormalLowMs: 30.0, IdleRttNormalHighMs: 45.0, IdleRttPoorMs: 80.0,
            IdleLossIdealPct: 0.2, IdleLossAcceptablePct: 0.5,
            LoadedLossDownLowPct: 0.5, LoadedLossDownHighPct: 1.0,
            LoadedLossUpLowPct: 0.25, LoadedLossUpHighPct: 0.5,
            LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 25.0,
            JitterIdealMs: 5.0, JitterTypicalMs: 6.5, JitterPoorMs: 15.0),

        AccessTechnology.DirectEthernet => new AccessProfile("Active Ethernet",
            IdleRttIdealMs: 0.5, IdleRttNormalLowMs: 1.0, IdleRttNormalHighMs: 3.0, IdleRttPoorMs: 8.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.05,
            LoadedLossDownLowPct: 1.0, LoadedLossDownHighPct: 2.0,
            LoadedLossUpLowPct: 0.5, LoadedLossUpHighPct: 1.0,
            LoadedDeltaExcellentMs: 2.0, LoadedDeltaAcceptableMs: 10.0,
            JitterIdealMs: 0.4, JitterTypicalMs: 0.7, JitterPoorMs: 3.0),

        AccessTechnology.FixedWireless => new AccessProfile("Fixed Wireless",
            IdleRttIdealMs: 5.0, IdleRttNormalLowMs: 8.0, IdleRttNormalHighMs: 15.0, IdleRttPoorMs: 35.0,
            IdleLossIdealPct: 0.3, IdleLossAcceptablePct: 0.5,
            LoadedLossDownLowPct: 2.0, LoadedLossDownHighPct: 4.0,
            LoadedLossUpLowPct: 1.0, LoadedLossUpHighPct: 2.0,
            LoadedDeltaExcellentMs: 10.0, LoadedDeltaAcceptableMs: 20.0,
            JitterIdealMs: 2.5, JitterTypicalMs: 4.0, JitterPoorMs: 12.0),

        AccessTechnology.Cellular => new AccessProfile("Cellular",
            IdleRttIdealMs: 20.0, IdleRttNormalLowMs: 25.0, IdleRttNormalHighMs: 50.0, IdleRttPoorMs: 90.0,
            IdleLossIdealPct: 0.3, IdleLossAcceptablePct: 0.5,
            LoadedLossDownLowPct: 2.0, LoadedLossDownHighPct: 4.0,
            LoadedLossUpLowPct: 2.0, LoadedLossUpHighPct: 4.0,
            LoadedDeltaExcellentMs: 20.0, LoadedDeltaAcceptableMs: 50.0,
            JitterIdealMs: 4.0, JitterTypicalMs: 6.0, JitterPoorMs: 20.0),

        AccessTechnology.Dsl => new AccessProfile("DSL",
            IdleRttIdealMs: 6.0, IdleRttNormalLowMs: 10.0, IdleRttNormalHighMs: 25.0, IdleRttPoorMs: 50.0,
            IdleLossIdealPct: 0.05, IdleLossAcceptablePct: 0.2,
            LoadedLossDownLowPct: 3.0, LoadedLossDownHighPct: 5.0,
            LoadedLossUpLowPct: 3.0, LoadedLossUpHighPct: 5.0,
            LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 20.0,
            JitterIdealMs: 1.0, JitterTypicalMs: 2.0, JitterPoorMs: 5.0),

        AccessTechnology.PppoE => NeutralProfile("PPPoE"),
        AccessTechnology.Other => NeutralProfile("Other"),
        _ => null
    };

    /// <summary>
    /// Wide-band profile for technologies where the underlying medium is ambiguous.
    /// Deliberately less discriminating so neither fiber nor DSL users are penalized.
    /// </summary>
    private static AccessProfile NeutralProfile(string displayName) => new(displayName,
        IdleRttIdealMs: 1.5, IdleRttNormalLowMs: 4.0, IdleRttNormalHighMs: 20.0, IdleRttPoorMs: 45.0,
        IdleLossIdealPct: 0.05, IdleLossAcceptablePct: 0.2,
        LoadedLossDownLowPct: 2.0, LoadedLossDownHighPct: 4.0,
        LoadedLossUpLowPct: 1.0, LoadedLossUpHighPct: 2.0,
        LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 20.0,
        IsNeutral: true);
}
