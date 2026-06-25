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
    public const double PonTxPowerHighDbm = 4.0;
    public const double PonTempHighC = 75.0;

    // --- Hysteresis ---
    public const double PowerHysteresisDbm = 2.0;
    public const double TempHysteresisC = 5.0;

    // --- Active Ethernet SFP thresholds ---
    public const double AeRxPowerLowDbm = -14.0;
    public const double AeTxPowerHighDbm = 1.0;
    public const double AeTempHighC = 80.0;

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
