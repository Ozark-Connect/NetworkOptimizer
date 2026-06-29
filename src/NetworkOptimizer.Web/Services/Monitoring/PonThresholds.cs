using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Single source-of-truth for the default PON and SFP optical thresholds. These
/// are the fallbacks used when the user hasn't overridden a value; the effective
/// thresholds are resolved by <see cref="SfpDdmThresholds"/>.
/// </summary>
public static class PonThresholds
{
    // --- PON thresholds (SFP and external ONT share these) ---
    public const double PonRxPowerLowDbm = -25.0;
    /// <summary>High ONU transmit power for GPON (Class B+ ONUs transmit ~+0.5 to +5 dBm).</summary>
    public const double PonTxPowerHighDbm = 4.0;
    /// <summary>High ONU transmit power for XGS-PON, which transmits hotter (~+4 to +9 dBm). Set to the
    /// same relative position in that range as GPON's +4 in +0.5..+5, so it flags before the +9 max.</summary>
    public const double XgsPonTxPowerHighDbm = 8.0;
    public const double PonTempHighC = 75.0;

    // --- PON receiver operating envelope (physics; consumed by ISP Health optical scoring) ---
    /// <summary>ONU receiver sensitivity floor (GPON/XGS-PON Class B+); at/below this the link risks dropping.</summary>
    public const double PonRxFloorDbm = -28.0;
    /// <summary>ONU receiver overload point; receive power hotter than this risks saturating the receiver.</summary>
    public const double PonRxOverloadDbm = -8.0;
    /// <summary>Coldest receive power a realistic single/cascaded 1:64 split can produce. Below this is
    /// excess loss (dirty connector, bend, splice), not a deeper splitter - 1:128 is not in the wild.</summary>
    public const double PonExcessLossFloorDbm = -25.5;

    // --- ONT error-counter spike thresholds (per poll) ---
    /// <summary>Per-poll FEC corrected-error spike threshold. Corrected errors are benign in small
    /// numbers (the FEC is doing its job); a sustained rate above this corroborates a marginal optic.</summary>
    public const long PonFecErrorSpikePerPoll = 1000;
    /// <summary>Per-poll BIP (bit-interleaved-parity) error threshold. BIP errors are uncorrected bit
    /// errors, so this is stricter than the FEC corrected-error threshold.</summary>
    public const long PonBipErrorSpikePerPoll = 100;

    // --- ONT error-count health for ISP Health (absolute count over the window, per-day). ---
    // A healthy PON link has a negligible BIP / uncorrectable-FEC count: 0 is ideal, a few per day
    // (~5-10) is still good, and above ~50/day is poor. BIP and uncorrectable FEC share this line.
    /// <summary>Per-day BIP/uncorrectable-FEC count that still scores well (a few errors/day; 0 is ideal).</summary>
    public const double PonErrorsPerDayGood = 10.0;
    /// <summary>Per-day BIP/uncorrectable-FEC count above which the link is poor (and an issue is raised).</summary>
    public const double PonErrorsPerDayPoor = 50.0;

    // --- Hysteresis ---
    public const double PowerHysteresisDbm = 2.0;
    public const double TempHysteresisC = 5.0;

    // --- Active Ethernet SFP thresholds ---
    public const double AeRxPowerLowDbm = -14.0;
    public const double AeTxPowerHighDbm = 1.0;
    public const double AeTempHighC = 80.0;

    // --- Active Ethernet receiver operating envelope ---
    public const double AeRxFloorDbm = -16.0;
    public const double AeRxOverloadDbm = -1.0;

    // --- Generic SFP thresholds ---
    public const double SfpTempHighGenericC = 87.0;
}

/// <summary>
/// Effective SFP DDM alert thresholds, per transceiver category. Each value falls
/// back to its <see cref="PonThresholds"/> default when the user hasn't set an
/// override in <see cref="MonitoringSettings"/>.
/// </summary>
public sealed record SfpDdmThresholds(
    double PonRxPowerLowDbm,
    double PonTxPowerHighDbm,
    double PonTempHighC,
    double AeRxPowerLowDbm,
    double AeTxPowerHighDbm,
    double AeTempHighC,
    double SfpTempHighGenericC)
{
    /// <summary>The built-in defaults, used when no monitoring settings exist.</summary>
    public static SfpDdmThresholds Defaults { get; } = new(
        PonThresholds.PonRxPowerLowDbm,
        PonThresholds.PonTxPowerHighDbm,
        PonThresholds.PonTempHighC,
        PonThresholds.AeRxPowerLowDbm,
        PonThresholds.AeTxPowerHighDbm,
        PonThresholds.AeTempHighC,
        PonThresholds.SfpTempHighGenericC);

    /// <summary>Resolves effective thresholds from settings, defaulting any unset value.</summary>
    public static SfpDdmThresholds FromSettings(MonitoringSettings s) => new(
        s.PonRxPowerLowDbm ?? PonThresholds.PonRxPowerLowDbm,
        s.PonTxPowerHighDbm ?? PonThresholds.PonTxPowerHighDbm,
        s.PonTempHighC ?? PonThresholds.PonTempHighC,
        s.AeRxPowerLowDbm ?? PonThresholds.AeRxPowerLowDbm,
        s.AeTxPowerHighDbm ?? PonThresholds.AeTxPowerHighDbm,
        s.AeTempHighC ?? PonThresholds.AeTempHighC,
        s.SfpTempHighGenericC ?? PonThresholds.SfpTempHighGenericC);
}
