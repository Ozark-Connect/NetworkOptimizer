using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Unit tests for the pure Access Layer Physical Link scorer: optical (PON/AE) margin-to-floor
/// grading, the bounded born-lossy bracket, the display-only split-ratio inference, DOCSIS RF/FEC
/// (3.0 vs 3.1) scoring, and cellular passthrough.
/// </summary>
public class PhysicalLinkScorerTests
{
    private const double Weight = 0.15;

    private static PhysicalLinkResult ScorePon(double rx, double? worst = null, double? baseline = null,
        bool? operational = null, double? tx = null, string ponType = "GPON",
        long? fecTotal = null, long? bipTotal = null, double windowDays = 2.0) =>
        PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Pon,
            SourceName = "Test ONT",
            RxPowerMedianDbm = rx,
            RxPowerWorstDbm = worst,
            RxPowerBaselineDbm = baseline,
            PonOperational = operational,
            TxPowerDbm = tx,
            PonType = ponType,
            FecErrorsTotal = fecTotal,
            BipErrorsTotal = bipTotal,
            WindowDays = windowDays
        }, expectedUploadMbps: null, Weight);

    // ---- PON receive-power grading ----

    [Fact]
    public void Pon_healthy_rx_scores_high_with_no_issues()
    {
        // TJ's real reading: -22 dBm on a 1:64 split is healthy with margin to the -28 floor.
        var result = ScorePon(-22.0);
        result.Factor.Score.Should().BeInRange(94, 98);
        result.Issues.Should().BeEmpty();
        result.Factor.Weight.Should().Be(Weight);
    }

    [Fact]
    public void Pon_more_insertion_loss_scores_slightly_lower_but_still_healthy()
    {
        // The gentle healthy slope: a deeper-loss but in-spec link reads a touch lower, never a flat 100.
        var shallow = ScorePon(-14.0).Factor.Score!.Value;   // ~1:16, low loss
        var deep = ScorePon(-22.0).Factor.Score!.Value;      // ~1:64, more loss
        shallow.Should().BeGreaterThan(deep);
        deep.Should().BeGreaterThan(90);
    }

    [Fact]
    public void Pon_marginal_rx_penalized_and_raises_warning()
    {
        var result = ScorePon(-26.0);
        result.Factor.Score.Should().BeInRange(40, 75);
        result.Issues.Should().Contain(i => i.Severity == IspIssueSeverity.Warning);
    }

    [Fact]
    public void Pon_below_floor_is_critical_and_near_zero()
    {
        var result = ScorePon(-28.5);
        result.Factor.Score.Should().BeLessThan(10);
        result.Issues.Should().Contain(i => i.Severity == IspIssueSeverity.Critical);
    }

    [Fact]
    public void Pon_born_lossy_bracket_flags_excess_loss_without_baseline()
    {
        // Colder than the coldest realistic 1:64 (~-25.5) is excess loss, not a bigger splitter.
        var result = ScorePon(-26.0);
        result.Issues.Should().Contain(i => i.Title.Contains("excess optical loss"));
    }

    [Fact]
    public void Pon_link_not_operational_caps_score_and_is_critical()
    {
        var result = ScorePon(-22.0, operational: false);
        result.Factor.Score.Should().BeLessThanOrEqualTo(10);
        result.Issues.Should().Contain(i => i.Severity == IspIssueSeverity.Critical && i.Title.Contains("dropped out of O5"));
    }

    [Fact]
    public void Pon_overload_is_penalized()
    {
        var result = ScorePon(-6.0);
        result.Factor.Score.Should().BeLessThan(95);
        result.Issues.Should().Contain(i => i.Title.Contains("too hot"));
    }

    [Fact]
    public void Pon_trend_drop_from_baseline_raises_degrading_issue()
    {
        var result = ScorePon(-22.0, baseline: -19.0);   // fell 3 dB from its own baseline
        result.Issues.Should().Contain(i => i.Title.Contains("degrading"));
    }

    [Fact]
    public void Pon_elevated_errors_cap_score_and_warn()
    {
        // A high absolute error count (well above the ~50/day poor line) caps the optical score and
        // raises an issue, even though the RX reading itself is fine.
        var result = ScorePon(-20.0, fecTotal: 1000, windowDays: 2);   // ~500/day
        result.Factor.Score.Should().BeLessThan(85);
        result.Issues.Should().Contain(i => i.Title.Contains("optical errors accumulating"));
    }

    [Fact]
    public void Pon_zero_errors_do_not_penalize()
    {
        var healthy = ScorePon(-20.0).Factor.Score!.Value;
        var withZeroErrors = ScorePon(-20.0, fecTotal: 0, bipTotal: 0, windowDays: 2);
        withZeroErrors.Factor.Score!.Value.Should().Be(healthy);
        withZeroErrors.Issues.Should().NotContain(i => i.Title.Contains("optical errors"));
    }

    [Fact]
    public void Pon_bip_errors_share_the_same_per_day_line_as_fec()
    {
        // BIP and uncorrectable FEC use the same per-day curve; a high BIP count raises the issue.
        var result = ScorePon(-20.0, bipTotal: 1000, windowDays: 2);   // ~500/day
        result.Issues.Should().Contain(i => i.Title.Contains("optical errors accumulating"));
    }

    [Fact]
    public void Pon_value_text_shows_tx_when_present()
    {
        ScorePon(-20.0, tx: 2.5).Factor.ValueText.Should().Contain("TX");
    }

    [Fact]
    public void Pon_healthy_steady_link_has_no_anomaly_note()
    {
        ScorePon(-22.0).Factor.Description.Should().NotContain("anomaly");
    }

    [Fact]
    public void Pon_dip_appends_anomaly_note_even_without_an_issue()
    {
        // Worst sample well below the (healthy) median: the worst-cap engaged, so a dip was factored
        // in even though no threshold-crossing issue was raised.
        var result = ScorePon(-22.0, worst: -24.0);
        result.Issues.Should().BeEmpty();
        result.Factor.Description.Should().Contain("anomaly");
    }

    [Fact]
    public void Pon_steady_marginal_rx_has_issue_but_no_anomaly_note()
    {
        // A steadily-low RX raises marginal/excess-loss issues but is NOT a transient event - it's
        // visible in the displayed value, so it must not get the "anomaly during this window" note.
        var result = ScorePon(-26.0);
        result.Issues.Should().NotBeEmpty();
        result.Factor.Description.Should().NotContain("anomaly");
    }

    [Fact]
    public void Docsis_steady_hot_power_has_no_anomaly_note()
    {
        var result = ScoreDocsis(dsPower: 10);
        result.Issues.Should().NotBeEmpty();
        result.Factor.Description.Should().NotContain("anomaly");
    }

    [Fact]
    public void Docsis_channel_loss_appends_anomaly_note()
    {
        ScoreDocsis(locked: 24, peak: 32).Factor.Description.Should().Contain("anomaly");
    }

    [Fact]
    public void Pon_high_tx_power_caps_and_warns()
    {
        var result = ScorePon(-22.0, tx: 6.0);   // above PonTxPowerHighDbm (4.0)
        result.Factor.Score.Should().BeLessThanOrEqualTo(75);
        result.Issues.Should().Contain(i => i.Title.Contains("transmit power high"));
    }

    [Fact]
    public void Pon_xgs_tolerates_higher_tx_than_gpon()
    {
        // +7 dBm ONU TX flags on GPON (>+4) but is normal on XGS-PON (<+8), which transmits hotter.
        PhysicalLinkResult Tx7(bool xgs) => PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Pon, SourceName = "ONT", RxPowerMedianDbm = -20.0, TxPowerDbm = 7.0, IsXgsPon = xgs
        }, null, Weight);

        Tx7(false).Issues.Should().Contain(i => i.Title.Contains("transmit power high"));
        Tx7(true).Issues.Should().NotContain(i => i.Title.Contains("transmit power high"));
    }

    // ---- Split-ratio inference (display only) ----

    [Theory]
    [InlineData(-16.0, "1:16")]
    [InlineData(-19.5, "1:32")]
    [InlineData(-20.5, "1:32")]   // field calibration: -20.5 stays in the 1:32 realm
    [InlineData(-21.0, "1:64")]
    [InlineData(-22.6, "1:64")]
    public void Split_ratio_inference_snaps_to_nearest_rung(double rx, string expected)
    {
        PhysicalLinkScorer.InferSplitRatio(rx, "GPON").Should().Contain(expected);
    }

    [Fact]
    public void Split_ratio_caps_at_1_64_for_very_cold_rx()
    {
        // Deeper than 1:64 is reported as 1:64+/excess, never 1:128.
        PhysicalLinkScorer.InferSplitRatio(-30.0, "GPON").Should().Contain("1:64+");
    }

    // ---- Active Ethernet ----

    [Fact]
    public void ActiveEthernet_healthy_rx_scores_high()
    {
        var result = PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.ActiveEthernet,
            SourceName = "AE SFP",
            RxPowerMedianDbm = -6.0
        }, null, Weight);
        result.Factor.Score.Should().BeGreaterThan(90);
        result.Issues.Should().BeEmpty();
    }

    // ---- DOCSIS ----

    private static PhysicalLinkResult ScoreDocsis(double? snr = 40, double? dsPower = 0, double? usPower = 44,
        long corr = 1_000_000, long unc = 0, int? locked = 32, int? peak = 32, bool? ofdma = true,
        double? expectedUp = null, double windowDays = 2.0) =>
        PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Docsis,
            SourceName = "Test CM",
            DsSnrDb = snr,
            DsPowerDbmv = dsPower,
            UsPowerDbmv = usPower,
            CorrectablesDelta = corr,
            UncorrectablesDelta = unc,
            LockedDsChannels = locked,
            PeakDsChannels = peak,
            OfdmaActive = ofdma,
            WindowDays = windowDays
        }, expectedUp, Weight);

    [Fact]
    public void Docsis_healthy_scores_high_with_no_issues()
    {
        var result = ScoreDocsis();
        result.Factor.Score.Should().BeGreaterThan(92);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Docsis_31_tolerates_a_higher_absolute_uncorrectable_rate()
    {
        // Same uncorrectable count: 3.1 OFDM processes ~10x the codewords, so its allowable per-day
        // rate is higher and it scores above 3.0 SC-QAM for the same absolute count.
        var ofdm = ScoreDocsis(corr: 1_000_000, unc: 30_000, ofdma: true).Factor.Score!.Value;
        var scqam = ScoreDocsis(corr: 1_000_000, unc: 30_000, ofdma: false).Factor.Score!.Value;
        ofdm.Should().BeGreaterThan(scqam);
    }

    [Fact]
    public void Docsis_plant_generation_inferred_from_plan_speed_when_no_ofdma_reading()
    {
        // No live OFDMA reading, but a >50 Mbps plan implies OFDMA - more codewords, higher allowable rate.
        var byPlan = ScoreDocsis(corr: 1_000_000, unc: 30_000, ofdma: null, expectedUp: 200).Factor.Score!.Value;
        var legacy = ScoreDocsis(corr: 1_000_000, unc: 30_000, ofdma: null, expectedUp: 20).Factor.Score!.Value;
        byPlan.Should().BeGreaterThan(legacy);
    }

    [Fact]
    public void Docsis_high_ratio_but_low_rate_scores_ok_and_no_issue()
    {
        // The real-world case: uncorrectables are a high fraction of ERRORED codewords (30%) but the
        // absolute rate is low. The rate half keeps the FEC score healthy and the issue (gated at the
        // 40% ratio line, here just below... actually 30% < 40%) does not fire.
        var result = ScoreDocsis(corr: 700, unc: 300, ofdma: true);   // ratio 30%, ~150/day
        result.Factor.Score.Should().BeGreaterThan(85);
        result.Issues.Should().NotContain(i => i.Title.Contains("uncorrectable"));
    }

    [Fact]
    public void Docsis_uncorrectable_ratio_over_40pct_raises_issue()
    {
        var result = ScoreDocsis(corr: 400, unc: 600, ofdma: true);   // ratio 60% > 40%
        result.Issues.Should().Contain(i => i.Title.Contains("uncorrectable"));
    }

    [Fact]
    public void Docsis_hot_downstream_recommends_attenuator()
    {
        var result = ScoreDocsis(dsPower: 10);
        result.Issues.Should().Contain(i => i.Title.Contains("downstream power too hot")
            && i.Recommendation!.Contains("attenuator"));
    }

    [Fact]
    public void Docsis_high_upstream_flags_straining()
    {
        var result = ScoreDocsis(usPower: 52);
        result.Issues.Should().Contain(i => i.Title.Contains("upstream transmit power high"));
    }

    [Fact]
    public void Docsis_channel_loss_caps_score()
    {
        var result = ScoreDocsis(locked: 24, peak: 32);   // dropped 8 channels
        result.Factor.Score.Should().BeLessThanOrEqualTo(40);
        result.Issues.Should().Contain(i => i.Title.Contains("channels dropped"));
    }

    [Fact]
    public void Docsis_no_metrics_yields_null_factor()
    {
        var result = PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Docsis,
            SourceName = "Dead CM"
        }, null, Weight);
        result.Factor.Score.Should().BeNull();
    }

    // ---- Cellular ----

    [Fact]
    public void Cellular_passes_signal_quality_through()
    {
        var result = PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Cellular,
            SourceName = "U5G",
            SignalQuality = 80,
            NetworkMode = "5G SA"
        }, null, Weight);
        result.Factor.Score.Should().Be(80);
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Cellular_poor_signal_raises_warning()
    {
        var result = PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Cellular,
            SourceName = "U5G",
            SignalQuality = 18
        }, null, Weight);
        result.Issues.Should().Contain(i => i.Severity == IspIssueSeverity.Warning);
    }

    [Fact]
    public void Cellular_5g_to_lte_downgrade_only_dings_when_5g_capable()
    {
        var capable = PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Cellular,
            SourceName = "U5G",
            SignalQuality = 70,
            NetworkModeDowngraded = true,
            Is5gCapable = true
        }, null, Weight);
        capable.Issues.Should().Contain(i => i.Title.Contains("downgraded to LTE"));

        var lteOnly = PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Cellular,
            SourceName = "LTE modem",
            SignalQuality = 70,
            NetworkModeDowngraded = true,
            Is5gCapable = false
        }, null, Weight);
        lteOnly.Issues.Should().NotContain(i => i.Title.Contains("downgraded"));
    }
}
