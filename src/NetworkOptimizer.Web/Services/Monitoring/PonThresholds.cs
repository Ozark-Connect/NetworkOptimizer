namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Single source-of-truth for PON and SFP optical thresholds.
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
