namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Single source-of-truth for DOCSIS cable-modem RF HEALTH thresholds used by ISP
/// Health physical-link scoring. These are distinct from the alert thresholds in
/// <c>CableModemAlertEvaluator</c> (which fire hard faults); these describe the
/// ideal/marginal/poor anchors a 0-100 health curve interpolates over.
///
/// Numbers are grounded in CableLabs DOCSIS PHY guidance and field practice:
/// DS power ideal -7..+7 dBmV (pad above +8); US power ideal 40..47 dBmV, straining
/// above 51; DS MER ideal ~40 dB, floor 33 (256-QAM SC-QAM) / 36 (4096-QAM OFDM).
/// On DOCSIS 3.1 OFDM/OFDMA with LDPC, high CORRECTABLE counts are normal/benign and
/// only the UNCORRECTABLE fraction matters; DOCSIS 3.0 SC-QAM is graded more strictly.
/// </summary>
public static class DocsisHealthThresholds
{
    // --- Downstream receive power (dBmV), per channel. Tent curve peaks near 0. ---
    public const double DsPowerIdealLowDbmv = -7.0;
    public const double DsPowerIdealHighDbmv = 7.0;
    /// <summary>Above this, recommend a forward-path attenuator (pad).</summary>
    public const double DsPowerPadAdviseDbmv = 8.0;
    public const double DsPowerStarvedDbmv = -10.0;
    public const double DsPowerOutOfSpecLowDbmv = -15.0;
    public const double DsPowerOutOfSpecHighDbmv = 15.0;

    // --- Upstream transmit power (dBmV). Higher beyond ideal = modem straining. ---
    public const double UsPowerIdealHighDbmv = 47.0;
    public const double UsPowerDriftingDbmv = 48.0;
    /// <summary>Marginal: running out of headroom (matches the existing alert line).</summary>
    public const double UsPowerMarginalDbmv = 51.0;
    public const double UsPowerCriticalDbmv = 53.0;
    public const double UsPowerMaxDbmv = 57.0;

    // --- Downstream MER/SNR (dB). Floor depends on plant generation. ---
    public const double DsMerIdealDb = 40.0;
    public const double DsMerFloorScQamDb = 33.0;   // 256-QAM SC-QAM (DOCSIS 3.0)
    public const double DsMerFloorOfdmDb = 36.0;    // 4096-QAM OFDM (DOCSIS 3.1)

    // --- FEC uncorrectable ratio = unc / (corr + unc) over the window. ---
    // The denominator is (corrected + uncorrected) errored codewords, NOT all codewords
    // (the time-series lacks error-free counts), so DOCSIS 3.0 sits inflated vs 3.1 and
    // gets its own stricter anchors.
    /// <summary>DOCSIS 3.1 OFDM: correctables benign; flag the uncorrectable fraction at ~1%.</summary>
    public const double FecUncorrRatioOfdmGood = 0.01;
    public const double FecUncorrRatioOfdmPoor = 0.10;
    /// <summary>DOCSIS 3.0 SC-QAM: strict; target uncorrectables near zero.</summary>
    public const double FecUncorrRatioScQamGood = 1e-4;
    public const double FecUncorrRatioScQamPoor = 1e-2;

    /// <summary>
    /// Provisioned upstream above this (Mbps) is a hint the plant is DOCSIS 3.1 OFDMA
    /// mid/high-split: a single legacy ATDMA channel caps ~27-30 Mbps and operators
    /// avoid stacking four in the noisy low-split band. A HINT only - an active OFDMA
    /// upstream channel in the live snapshot is the authoritative tell.
    /// </summary>
    public const double OfdmaLikelyUpstreamMbps = 50.0;
}
