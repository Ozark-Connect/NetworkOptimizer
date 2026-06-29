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
    Cellular
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

    /// <summary>Transmit optical power (dBm); penalized only above the category high threshold.</summary>
    public double? TxPowerDbm { get; init; }

    /// <summary>O5 (Operation) state graded across the window from the link-status series: true when
    /// every reported state was Operation, false when the link broke out of O5 at least once (a hard
    /// fault), null when the source never reports an O-state (DDM sticks, most ONTs). Polls that omit
    /// the status are ignored, so a missing reading never reads as a link-down.</summary>
    public bool? PonOperational { get; init; }

    /// <summary>PON type for display (GPON, XGS-PON); also selects the OLT-launch assumption.</summary>
    public string? PonType { get; init; }

    /// <summary>True when the configured access technology is XGS-PON (reliable even when the device
    /// does not report PonType). Selects the higher XGS-PON ONU transmit-power threshold.</summary>
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
}

/// <summary>The Physical Link factor plus any issues it raised.</summary>
public sealed record PhysicalLinkResult(IspScoreFactor Factor, List<IspHealthIssue> Issues);

/// <summary>
/// One selectable physical-link source for the ambiguity picker. <see cref="Key"/> is the
/// persisted token ("cm:3", "ont:2", "modem:1", "sfp:aa:bb:cc:dd:ee:ff/Port 1").
/// </summary>
public sealed record PhysicalLinkCandidate(string Key, string Label);
