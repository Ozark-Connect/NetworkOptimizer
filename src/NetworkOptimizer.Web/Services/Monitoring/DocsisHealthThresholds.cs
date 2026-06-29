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

    // --- FEC scoring: a 50/50 blend of the uncorrectable RATIO and the uncorrectable RATE. ---
    // The ratio unc / (corr + unc) is "how dominant are uncorrectables among errored codewords" -
    // a texture signal, NOT unc/total-codewords (the time-series lacks error-free counts), so a
    // moderate ratio is normal when the absolute rate is low. The rate (uncorrectables per day) is
    // the magnitude gate. Both halves average into the FEC sub-score.
    /// <summary>Uncorrectable fraction of errored codewords that still scores well (texture, not a fault).</summary>
    public const double FecUncorrRatioGood = 0.30;
    /// <summary>Uncorrectable fraction at which the ratio half bottoms out.</summary>
    public const double FecUncorrRatioPoor = 0.90;
    /// <summary>Uncorrectable fraction above which an issue is raised regardless of rate.</summary>
    public const double FecUncorrRatioIssue = 0.40;
    // Uncorrectable codewords per day - the magnitude gate. DOCSIS 3.1 OFDM runs ~10x the throughput
    // of 3.0 SC-QAM, so it processes ~10x the codewords and a healthy 3.1 link logs proportionally
    // more absolute uncorrectables; its allowable rate scales up accordingly.
    /// <summary>DOCSIS 3.0 SC-QAM: uncorrectables/day that are fine.</summary>
    public const double UncorrPerDayGoodScQam = 2000.0;
    /// <summary>DOCSIS 3.0 SC-QAM: uncorrectables/day at which the rate half bottoms out (and an issue is raised).</summary>
    public const double UncorrPerDayPoorScQam = 20000.0;
    /// <summary>DOCSIS 3.1 OFDM: uncorrectables/day that are fine (~10x SC-QAM).</summary>
    public const double UncorrPerDayGoodOfdm = 20000.0;
    /// <summary>DOCSIS 3.1 OFDM: uncorrectables/day at which the rate half bottoms out (and an issue is raised).</summary>
    public const double UncorrPerDayPoorOfdm = 200000.0;

    /// <summary>
    /// Provisioned upstream above this (Mbps) is a hint the plant is DOCSIS 3.1 OFDMA
    /// mid/high-split: a single legacy ATDMA channel caps ~27-30 Mbps and operators
    /// avoid stacking four in the noisy low-split band. A HINT only - an active OFDMA
    /// upstream channel in the live snapshot is the authoritative tell.
    /// </summary>
    public const double OfdmaLikelyUpstreamMbps = 50.0;
}
