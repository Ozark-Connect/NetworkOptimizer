using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// All ISP Health tunables in one place: score weights, load classification
/// thresholds, congestion and step-change detector parameters, and the SQM
/// recommendation rule. The authoritative reference for these values is
/// research/isp-health-spec.md (local-only); keep both in sync when tuning.
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
    public double IdleLatencyWeight { get; set; } = 0.15;

    /// <summary>Weight of idle packet loss within the access dimension.</summary>
    public double IdleLossWeight { get; set; } = 0.15;

    /// <summary>Weight of loaded latency delta within the access dimension.</summary>
    public double LoadedLatencyWeight { get; set; } = 0.20;

    /// <summary>Weight of loaded packet loss within the access dimension.</summary>
    public double LoadedLossWeight { get; set; } = 0.20;

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
    /// SNMP interface counters lag behind real-time ping probes by several seconds.
    /// When classifying a latency/loss sample as idle or loaded, the sample's timestamp
    /// is shifted back by this amount so it aligns with the counter window that reflects
    /// the actual throughput at the time the sample was taken.
    /// </summary>
    public int CounterLagOffsetSeconds { get; set; } = 4;

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
    bool IsNeutral = false);

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
            LoadedDeltaExcellentMs: 2.0, LoadedDeltaAcceptableMs: 10.0),

        AccessTechnology.XgsPon => new AccessProfile("XGS-PON",
            IdleRttIdealMs: 1.5, IdleRttNormalLowMs: 2.0, IdleRttNormalHighMs: 3.0, IdleRttPoorMs: 8.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.05,
            LoadedLossDownLowPct: 0.5, LoadedLossDownHighPct: 1.0,
            LoadedLossUpLowPct: 0.25, LoadedLossUpHighPct: 0.5,
            LoadedDeltaExcellentMs: 2.0, LoadedDeltaAcceptableMs: 10.0),

        AccessTechnology.Docsis => new AccessProfile("DOCSIS",
            IdleRttIdealMs: 7.0, IdleRttNormalLowMs: 9.0, IdleRttNormalHighMs: 12.0, IdleRttPoorMs: 25.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.2,
            LoadedLossDownLowPct: 3.0, LoadedLossDownHighPct: 5.0,
            LoadedLossUpLowPct: 3.0, LoadedLossUpHighPct: 5.0,
            LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 20.0),

        AccessTechnology.Satellite => new AccessProfile("Satellite (LEO)",
            IdleRttIdealMs: 23.0, IdleRttNormalLowMs: 30.0, IdleRttNormalHighMs: 45.0, IdleRttPoorMs: 80.0,
            IdleLossIdealPct: 0.2, IdleLossAcceptablePct: 0.5,
            LoadedLossDownLowPct: 0.5, LoadedLossDownHighPct: 1.0,
            LoadedLossUpLowPct: 0.25, LoadedLossUpHighPct: 0.5,
            LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 25.0),

        AccessTechnology.DirectEthernet => new AccessProfile("Active Ethernet",
            IdleRttIdealMs: 0.5, IdleRttNormalLowMs: 1.0, IdleRttNormalHighMs: 3.0, IdleRttPoorMs: 8.0,
            IdleLossIdealPct: 0.02, IdleLossAcceptablePct: 0.05,
            LoadedLossDownLowPct: 1.0, LoadedLossDownHighPct: 2.0,
            LoadedLossUpLowPct: 0.5, LoadedLossUpHighPct: 1.0,
            LoadedDeltaExcellentMs: 2.0, LoadedDeltaAcceptableMs: 10.0),

        AccessTechnology.FixedWireless => new AccessProfile("Fixed Wireless",
            IdleRttIdealMs: 5.0, IdleRttNormalLowMs: 8.0, IdleRttNormalHighMs: 15.0, IdleRttPoorMs: 35.0,
            IdleLossIdealPct: 0.3, IdleLossAcceptablePct: 0.5,
            LoadedLossDownLowPct: 2.0, LoadedLossDownHighPct: 4.0,
            LoadedLossUpLowPct: 1.0, LoadedLossUpHighPct: 2.0,
            LoadedDeltaExcellentMs: 10.0, LoadedDeltaAcceptableMs: 20.0),

        AccessTechnology.Cellular => new AccessProfile("Cellular",
            IdleRttIdealMs: 20.0, IdleRttNormalLowMs: 25.0, IdleRttNormalHighMs: 50.0, IdleRttPoorMs: 90.0,
            IdleLossIdealPct: 0.3, IdleLossAcceptablePct: 0.5,
            LoadedLossDownLowPct: 2.0, LoadedLossDownHighPct: 4.0,
            LoadedLossUpLowPct: 2.0, LoadedLossUpHighPct: 4.0,
            LoadedDeltaExcellentMs: 20.0, LoadedDeltaAcceptableMs: 50.0),

        AccessTechnology.Dsl => new AccessProfile("DSL",
            IdleRttIdealMs: 6.0, IdleRttNormalLowMs: 10.0, IdleRttNormalHighMs: 25.0, IdleRttPoorMs: 50.0,
            IdleLossIdealPct: 0.05, IdleLossAcceptablePct: 0.2,
            LoadedLossDownLowPct: 3.0, LoadedLossDownHighPct: 5.0,
            LoadedLossUpLowPct: 3.0, LoadedLossUpHighPct: 5.0,
            LoadedDeltaExcellentMs: 5.0, LoadedDeltaAcceptableMs: 20.0),

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
