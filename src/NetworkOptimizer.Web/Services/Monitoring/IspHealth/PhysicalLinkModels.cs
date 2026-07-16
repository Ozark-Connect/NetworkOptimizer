using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>The access medium of the matched physical link.</summary>
public enum PhysicalMedium
{
    /// <summary>GPON / XGS-PON optical (SFP ONT or external ONT).</summary>
    Pon,
    /// <summary>Active Ethernet optical SFP.</summary>
    ActiveEthernet,
    /// <summary>DOCSIS cable modem.</summary>
    Docsis,
    /// <summary>Cellular (LTE / 5G) modem.</summary>
    Cellular,
    /// <summary>Satellite (Starlink) terminal.</summary>
    Satellite
}

/// <summary>
/// Window-aggregated physical-link metrics for the ONE source matched to the WAN,
/// assembled by <see cref="IspHealthService"/> from time-series plus the live poll
/// snapshot. Consumed by <see cref="PhysicalLinkScorer"/> to produce the Access Layer
/// Physical Link factor. Every field is optional so a partially-reporting source still
/// scores on what it has; a medium with no usable signal yields a null factor score.
/// </summary>
public class PhysicalLinkInput
{
    public required PhysicalMedium Medium { get; init; }

    /// <summary>Friendly source name for the factor verbiage (e.g. "AT&amp;T BGW320").</summary>
    public required string SourceName { get; init; }

    // --- Optical (PON / Active Ethernet) ---

    /// <summary>Median ONT/SFP receive optical power over the window (dBm).</summary>
    public double? RxPowerMedianDbm { get; init; }

    /// <summary>Worst (coldest) sustained receive power in the window, for the worst-case cap.</summary>
    public double? RxPowerWorstDbm { get; init; }

    /// <summary>Earliest-window median receive power, the baseline the trend check compares against.</summary>
    public double? RxPowerBaselineDbm { get; init; }

    /// <summary>Median transmit optical power over the window (dBm) - the typical level, shown in the
    /// factor value. Not the scoring input: the transmit-power-high rule grades the spike below.</summary>
    public double? TxPowerDbm { get; init; }

    /// <summary>Highest CLEAN transmit optical power in the window (dBm), the spike the transmit-power-high
    /// rule grades - the transmit counterpart of <see cref="RxPowerWorstDbm"/>. Artifact reads are
    /// dropped first so a lone DDM glitch can't trip the rule; a real or sustained hot laser still does.</summary>
    public double? TxPowerSpikeDbm { get; init; }

    /// <summary>O5 (Operation) state graded across the window from the link-status series: true when
    /// every reported state was Operation, false when the link broke out of O5 at least once (a hard
    /// fault), null when the source never reports an O-state (DDM sticks, most ONTs). Polls that omit
    /// the status are ignored, so a missing reading never reads as a link-down.</summary>
    public bool? PonOperational { get; init; }

    /// <summary>True when the configured access technology is XGS-PON. Drives the GPON/XGS-PON
    /// display copy, the OLT-launch assumption for the split-ratio estimate, and the higher
    /// XGS-PON ONU transmit-power threshold - the user's selection, not the device's self-report,
    /// which many ONTs omit or report inconsistently.</summary>
    public bool IsXgsPon { get; init; }

    /// <summary>Total uncorrectable-FEC codewords over the window (external ONT only). Graded as an
    /// absolute per-day count - negligible on a healthy plant. Null when not reported.</summary>
    public long? FecErrorsTotal { get; init; }

    /// <summary>Total BIP (uncorrected bit) errors over the window (external ONT only).</summary>
    public long? BipErrorsTotal { get; init; }

    /// <summary>Length of the scoring window in days, for normalizing error counts to a per-day rate.</summary>
    public double WindowDays { get; init; }

    // --- DOCSIS ---

    /// <summary>Downstream MER/SNR averaged over locked channels (dB).</summary>
    public double? DsSnrDb { get; init; }

    /// <summary>Downstream receive power averaged over locked channels (dBmV).</summary>
    public double? DsPowerDbmv { get; init; }

    /// <summary>Upstream transmit power averaged over locked channels (dBmV).</summary>
    public double? UsPowerDbmv { get; init; }

    /// <summary>Correctable codeword delta over the window.</summary>
    public long? CorrectablesDelta { get; init; }

    /// <summary>Uncorrectable codeword delta over the window.</summary>
    public long? UncorrectablesDelta { get; init; }

    /// <summary>Locked downstream channel count (latest in window).</summary>
    public int? LockedDsChannels { get; init; }

    /// <summary>Peak locked downstream channel count seen in the window.</summary>
    public int? PeakDsChannels { get; init; }

    /// <summary>Authoritative plant-generation tell: an active OFDMA upstream channel (live snapshot).</summary>
    public bool? OfdmaActive { get; init; }

    /// <summary>Modem model for display and the mid/high-split-only hint.</summary>
    public string? ModemModel { get; init; }

    // --- Cellular ---

    /// <summary>Composite cellular signal quality 0-100, as computed by CellularModemStats.</summary>
    public int? SignalQuality { get; init; }

    /// <summary>Active network mode for display (LTE, 5G NSA, 5G SA).</summary>
    public string? NetworkMode { get; init; }

    /// <summary>True when a 5G-&gt;LTE mode downgrade was observed in the window.</summary>
    public bool NetworkModeDowngraded { get; init; }

    /// <summary>True when the modem is 5G-capable (so a downgrade is meaningful).</summary>
    public bool Is5gCapable { get; init; }

    // --- Satellite (Starlink) ---

    /// <summary>Median fraction of sky time obstructed over the window (0..1).</summary>
    public double? ObstructionFraction { get; init; }

    /// <summary>Whether the dish reported itself obstructed on the latest poll.</summary>
    public bool? CurrentlyObstructed { get; init; }

    /// <summary>Mean dish-to-ground ping drop rate over the window (0..1), from the dish's own 1 Hz history.</summary>
    public double? DishDropRateAvg { get; init; }

    /// <summary>Worst 1-second dish drop rate seen in the window (0..1).</summary>
    public double? DishDropRateMax { get; init; }

    /// <summary>Dish-logged outage seconds over the window, normalized per day via <see cref="WindowDays"/>.</summary>
    public double? OutageSecondsTotal { get; init; }

    /// <summary>Dish-logged outage count over the window.</summary>
    public long? OutageCountTotal { get; init; }

    /// <summary>True when the dish reports persistently low SNR (live snapshot).</summary>
    public bool? SnrPersistentlyLow { get; init; }

    /// <summary>Alert names currently raised by the dish (live snapshot), e.g. "thermal_throttle".</summary>
    public IReadOnlyList<string>? DishAlerts { get; init; }

    /// <summary>Negotiated dish-to-router Ethernet speed (live snapshot), for the slow-link advisory.</summary>
    public int? EthSpeedMbps { get; init; }
}

/// <summary>The Physical Link factor plus any issues it raised.</summary>
public sealed record PhysicalLinkResult(IspScoreFactor Factor, List<IspHealthIssue> Issues);

/// <summary>
/// One selectable physical-link source for the ambiguity picker. <see cref="Key"/> is the
/// persisted token ("cm:3", "ont:2", "modem:1", "sfp:aa:bb:cc:dd:ee:ff/Port 1").
/// </summary>
public sealed record PhysicalLinkCandidate(string Key, string Label);
