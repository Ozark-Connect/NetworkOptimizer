using System.Globalization;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Pure scorer for the Access Layer "Physical Link" factor. Given window-aggregated
/// metrics for the one source matched to the WAN, produces a 0-100 sub-score plus any
/// issues. No I/O; fully unit-testable.
///
/// Design (see research/isp-health/physical-link-access-scoring.md):
/// - PON/AE grade on ABSOLUTE receive power margin-to-floor (a gentle healthy slope so
///   more insertion loss reads slightly lower, never a flat 100), with hard caps for a
///   down PON link, hot TX, or high temperature. The inferred splitter ratio is
///   DISPLAY-ONLY verbiage and never feeds the score. A reading colder than the coldest
///   any realistic 1:64 split can produce is a bounded, baseline-free excess-loss flag.
/// - DOCSIS grades MER, the FEC uncorrectable ratio (tolerant on 3.1 OFDM where
///   correctables are benign, strict on 3.0 SC-QAM), and DS/US power, with a channel-loss
///   cap. Plant generation is inferred from an active OFDMA channel, else plan speed.
/// - Cellular passes the existing composite signal quality through.
/// Reuses <see cref="PonThresholds"/> and <see cref="DocsisHealthThresholds"/> as the
/// single source-of-truth for breach anchors.
/// </summary>
public static class PhysicalLinkScorer
{
    /// <summary>Coldest receive power (dBm) any realistic single/cascaded 1:64 PON split can
    /// produce (min OLT launch, max splitter + drop loss). Below this is excess loss, not a
    /// bigger splitter - 1:128 is not seen in residential. Bounded, needs no baseline.</summary>
    private const double PonExcessLossFloorDbm = -25.5;

    /// <summary>An RX drop of at least this many dB from the link's own baseline is developing loss.</summary>
    private const double PonTrendDropDbm = 2.5;

    /// <summary>Standard PON splitter rungs (ratio, typical insertion loss dB incl. excess), capped at 1:64.</summary>
    private static readonly (int Ratio, double LossDb)[] SplitterRungs =
    {
        (2, 3.6), (4, 7.2), (8, 10.5), (16, 13.8), (32, 17.1), (64, 20.4)
    };

    public static PhysicalLinkResult Score(PhysicalLinkInput input, double? expectedUploadMbps, double factorWeight)
    {
        return input.Medium switch
        {
            PhysicalMedium.Pon => ScoreOptical(input, factorWeight, isPon: true),
            PhysicalMedium.ActiveEthernet => ScoreOptical(input, factorWeight, isPon: false),
            PhysicalMedium.Docsis => ScoreDocsis(input, expectedUploadMbps, factorWeight),
            PhysicalMedium.Cellular => ScoreCellular(input, factorWeight),
            _ => new PhysicalLinkResult(NullFactor(factorWeight, "no usable physical-link data"), new())
        };
    }

    // ---------------------------------------------------------------------------
    // Optical: PON and Active Ethernet
    // ---------------------------------------------------------------------------

    private static PhysicalLinkResult ScoreOptical(PhysicalLinkInput input, double factorWeight, bool isPon)
    {
        var issues = new List<IspHealthIssue>();
        var thresholds = input.OpticalThresholds ?? SfpDdmThresholds.Defaults;
        var rx = input.RxPowerMedianDbm;
        var label = isPon ? (FormatPonType(input.PonType) ?? "PON") : "Active Ethernet";

        if (rx is null)
            return new PhysicalLinkResult(NullFactor(factorWeight, $"{label} link not reporting optical power yet"), issues);

        // Absolute receive-power score: gentle healthy slope, knee at the marginal anchor,
        // zero at the receiver sensitivity floor. Warmer = healthier until overload.
        var rxLow = isPon ? thresholds.PonRxPowerLowDbm : thresholds.AeRxPowerLowDbm;
        var floor = isPon ? -28.0 : -16.0;             // receiver sensitivity (GPON/XGS-PON; AE)
        var overload = isPon ? -8.0 : -1.0;            // too hot
        var healthyTop = isPon ? -24.0 : -13.0;        // top of the gentle in-spec slope

        var score = ScoreCurve.Interpolate(rx.Value,
            (floor, 0),
            (floor + 1, 30),
            (rxLow, 90),
            (healthyTop, 95),
            (overload, 100));

        // Overload: penalize receive power hotter than the overload point (rare on PON).
        var overloadScore = ScoreCurve.Interpolate(rx.Value,
            (overload, 100), (overload + 2, 80), (overload + 5, 30), (overload + 8, 0));
        score = Math.Min(score, overloadScore);

        // Worst-sustained cap: a sustained excursion colder than the median pulls the score down.
        if (input.RxPowerWorstDbm is double worst && worst < rx.Value)
        {
            var worstScore = ScoreCurve.Interpolate(worst,
                (floor, 0), (floor + 1, 30), (rxLow, 90), (healthyTop, 95), (overload, 100));
            score = Math.Min(score, 0.5 * (score + worstScore));
        }

        var rxText = $"{rx.Value.ToString("0.0", CultureInfo.InvariantCulture)} dBm RX";
        var detailBits = new List<string>();

        // Receive-power issues.
        if (rx.Value <= floor)
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Critical,
                Title = $"{label}: optical signal below receiver sensitivity",
                Description = $"{input.SourceName} receive power is {rxText}, at or below the receiver floor (~{floor:0} dBm). The link is at risk of dropping.",
                Recommendation = "Inspect the fiber path: a dirty or loose connector, a macrobend, or a degraded splice can add several dB of loss."
            });
        else if (rx.Value <= rxLow)
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = $"{label}: marginal optical receive power",
                Description = $"{input.SourceName} receive power is {rxText}, past the marginal threshold ({rxLow:0} dBm) and approaching the receiver floor.",
                Recommendation = "Check the optical path for added loss (connectors, bends, splices)."
            });
        else if (rx.Value >= overload)
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = $"{label}: optical receive power too hot",
                Description = $"{input.SourceName} receive power is {rxText}, near the receiver overload point ({overload:0} dBm).",
                Recommendation = "An optical attenuator may be needed if the ONT sits very close to the OLT/splitter."
            });

        // PON-only: inferred split ratio (display verbiage) and bounded excess-loss flag.
        if (isPon)
        {
            var split = InferSplitRatio(rx.Value, input.PonType);
            if (split != null) detailBits.Add(split);

            if (rx.Value < PonExcessLossFloorDbm && rx.Value > floor)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "PON: excess optical loss",
                    Description = $"{input.SourceName} receive power ({rxText}) is colder than the deepest realistic splitter (1:64) should produce, which points to extra loss on the drop rather than a larger split.",
                    Recommendation = "Inspect the drop: a dirty/loose connector, a macrobend, or a degrading splice is the usual cause."
                });

            // Developing loss: a drop from the link's own baseline, independent of absolute level.
            if (input.RxPowerBaselineDbm is double baseline && rx.Value <= baseline - PonTrendDropDbm)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "PON: receive power is degrading",
                    Description = $"{input.SourceName} receive power fell {(baseline - rx.Value).ToString("0.0", CultureInfo.InvariantCulture)} dB over the window (from {baseline.ToString("0.0", CultureInfo.InvariantCulture)} to {rx.Value.ToString("0.0", CultureInfo.InvariantCulture)} dBm).",
                    Recommendation = "A trending drop usually means a connector or splice is degrading - inspect the optical path."
                });
        }

        // Caps and secondary signals.
        if (input.PonOperational == false)
        {
            score = Math.Min(score, 10);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Critical,
                Title = "PON: link not in operation",
                Description = $"{input.SourceName} PON link is not in the Operation (O5) state - the optical link is down or re-ranging.",
                Recommendation = "Check the ONT and the fiber; persistent non-O5 state means no service."
            });
        }

        var txHigh = isPon ? thresholds.PonTxPowerHighDbm : thresholds.AeTxPowerHighDbm;
        if (input.TxPowerDbm is double tx && tx > txHigh)
        {
            score = Math.Min(score, 75);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = $"{label}: transmit power high",
                Description = $"{input.SourceName} transmit optical power is {tx.ToString("0.0", CultureInfo.InvariantCulture)} dBm, above the {txHigh:0} dBm threshold.",
                Recommendation = "A consistently high TX can indicate the laser is compensating for path loss; inspect the optical path."
            });
        }

        var tempHigh = isPon ? thresholds.PonTempHighC : thresholds.AeTempHighC;
        if (input.TemperatureC is double temp)
        {
            if (temp >= tempHigh)
            {
                score = Math.Min(score, 70);
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = $"{label}: transceiver hot",
                    Description = $"{input.SourceName} transceiver temperature is {temp.ToString("0.0", CultureInfo.InvariantCulture)} C, at or above the {tempHigh:0} C threshold.",
                    Recommendation = "Improve airflow around the SFP/ONT; sustained heat shortens optic life and can raise errors."
                });
            }
            else
            {
                detailBits.Add($"{temp.ToString("0", CultureInfo.InvariantCulture)} C");
            }
        }

        var value = rxText;
        var desc = $"{label} optical receive power scored on margin to the receiver floor"
                   + (detailBits.Count > 0 ? $" ({string.Join(", ", detailBits)})." : ".");

        return new PhysicalLinkResult(
            Factor(factorWeight, (int)Math.Round(score), value, desc), issues);
    }

    /// <summary>
    /// Inferred EFFECTIVE split ratio for display only. Back-calculates splitter loss from a
    /// mid-class OLT launch assumption and typical drop loss, snaps to the nearest standard
    /// rung (capped at 1:64), and states the assumption so an SME can recalibrate. Never feeds
    /// the score. Returns null when the math lands outside the realistic range.
    /// </summary>
    internal static string? InferSplitRatio(double rxDbm, string? ponType)
    {
        var isXgs = (ponType ?? "").Contains("XGS", StringComparison.OrdinalIgnoreCase)
                    || (ponType ?? "").Contains("10G", StringComparison.OrdinalIgnoreCase);
        var oltTx = isXgs ? 5.0 : 3.5;        // mid-class launch (XGS-PON N2 / GPON B+)
        const double distLoss = 4.0;          // typical drop: fiber + connectors + splices
        var impliedLoss = oltTx - rxDbm - distLoss;

        if (impliedLoss < SplitterRungs[0].LossDb - 2.0) return null;  // too hot for even 1:2
        if (impliedLoss > SplitterRungs[^1].LossDb + 2.0)
            return "est. 1:64+ split or excess loss";

        var best = SplitterRungs[0];
        foreach (var rung in SplitterRungs)
            if (Math.Abs(rung.LossDb - impliedLoss) < Math.Abs(best.LossDb - impliedLoss))
                best = rung;

        return $"est. 1:{best.Ratio} split";
    }

    private static string? FormatPonType(string? ponType)
    {
        if (string.IsNullOrWhiteSpace(ponType)) return null;
        return ponType.Trim();
    }

    // ---------------------------------------------------------------------------
    // DOCSIS
    // ---------------------------------------------------------------------------

    private static PhysicalLinkResult ScoreDocsis(PhysicalLinkInput input, double? expectedUploadMbps, double factorWeight)
    {
        var issues = new List<IspHealthIssue>();
        var isOfdm = IsDocsis31(input, expectedUploadMbps);

        var parts = new List<(double Score, double Weight)>();
        var valueBits = new List<string>();

        // Downstream MER/SNR.
        if (input.DsSnrDb is double snr)
        {
            var floor = isOfdm ? DocsisHealthThresholds.DsMerFloorOfdmDb : DocsisHealthThresholds.DsMerFloorScQamDb;
            var snrScore = ScoreCurve.Interpolate(snr,
                (floor - 5, 0), (floor - 2, 30), (floor, 85), (DocsisHealthThresholds.DsMerIdealDb, 100));
            parts.Add((snrScore, 0.40));
            valueBits.Add($"SNR {snr.ToString("0.0", CultureInfo.InvariantCulture)} dB");
            if (snr < floor)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "DOCSIS: low downstream SNR",
                    Description = $"{input.SourceName} downstream MER/SNR is {snr.ToString("0.0", CultureInfo.InvariantCulture)} dB, below the {floor:0} dB floor for this plant.",
                    Recommendation = "Low SNR drives uncorrectable errors; check for ingress, corrosion, or a failing amplifier."
                });
        }

        // FEC uncorrectable ratio.
        if (input.CorrectablesDelta is long corr && input.UncorrectablesDelta is long unc)
        {
            var denom = corr + unc;
            double fecScore;
            if (denom <= 0)
            {
                fecScore = 100;
            }
            else
            {
                var ratio = (double)unc / denom;
                var good = isOfdm ? DocsisHealthThresholds.FecUncorrRatioOfdmGood : DocsisHealthThresholds.FecUncorrRatioScQamGood;
                var poor = isOfdm ? DocsisHealthThresholds.FecUncorrRatioOfdmPoor : DocsisHealthThresholds.FecUncorrRatioScQamPoor;
                fecScore = ScoreCurve.Interpolate(ratio, (0, 100), (good, 92), (poor, 25), (poor * 5, 0));
                if (ratio >= poor && unc > 0)
                    issues.Add(new IspHealthIssue
                    {
                        Severity = IspIssueSeverity.Warning,
                        Title = "DOCSIS: uncorrectable errors",
                        Description = $"{input.SourceName} uncorrectable codewords are {(ratio * 100).ToString("0.##", CultureInfo.InvariantCulture)}% of errored codewords over the window"
                                      + (isOfdm ? " (correctables are normal on DOCSIS 3.1 and not counted)." : "."),
                        Recommendation = "Sustained uncorrectables mean data loss; check downstream SNR and the coax/connectors for ingress."
                    });
            }
            parts.Add((fecScore, 0.30));
        }

        // Downstream power: tent centered near 0 dBmV.
        if (input.DsPowerDbmv is double dsp)
        {
            var dsScore = ScoreCurve.Interpolate(dsp,
                (DocsisHealthThresholds.DsPowerOutOfSpecLowDbmv, 0),
                (DocsisHealthThresholds.DsPowerStarvedDbmv, 60),
                (DocsisHealthThresholds.DsPowerIdealLowDbmv, 90),
                (0, 100),
                (DocsisHealthThresholds.DsPowerIdealHighDbmv, 90),
                (DocsisHealthThresholds.DsPowerPadAdviseDbmv, 80),
                (12, 40),
                (DocsisHealthThresholds.DsPowerOutOfSpecHighDbmv, 0));
            parts.Add((dsScore, 0.15));
            valueBits.Add($"DS {dsp.ToString("0.0", CultureInfo.InvariantCulture)} dBmV");
            if (dsp > DocsisHealthThresholds.DsPowerPadAdviseDbmv)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "DOCSIS: downstream power too hot",
                    Description = $"{input.SourceName} downstream receive power is {dsp.ToString("0.0", CultureInfo.InvariantCulture)} dBmV, above +{DocsisHealthThresholds.DsPowerPadAdviseDbmv:0} dBmV.",
                    Recommendation = "Add a forward-path attenuator (pad) to bring downstream power back toward 0 dBmV."
                });
            else if (dsp < DocsisHealthThresholds.DsPowerStarvedDbmv)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "DOCSIS: downstream power starved",
                    Description = $"{input.SourceName} downstream receive power is {dsp.ToString("0.0", CultureInfo.InvariantCulture)} dBmV, below {DocsisHealthThresholds.DsPowerStarvedDbmv:0} dBmV.",
                    Recommendation = "Remove an unnecessary splitter or check the drop for excess loss."
                });
        }

        // Upstream power: straining as it climbs past ideal.
        if (input.UsPowerDbmv is double usp)
        {
            var usScore = ScoreCurve.Interpolate(usp,
                (30, 100),
                (DocsisHealthThresholds.UsPowerIdealHighDbmv, 95),
                (DocsisHealthThresholds.UsPowerDriftingDbmv, 90),
                (DocsisHealthThresholds.UsPowerMarginalDbmv, 55),
                (DocsisHealthThresholds.UsPowerCriticalDbmv, 25),
                (DocsisHealthThresholds.UsPowerMaxDbmv, 0));
            parts.Add((usScore, 0.15));
            valueBits.Add($"US {usp.ToString("0.0", CultureInfo.InvariantCulture)} dBmV");
            if (usp > DocsisHealthThresholds.UsPowerMarginalDbmv)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "DOCSIS: upstream transmit power high",
                    Description = $"{input.SourceName} upstream transmit power is {usp.ToString("0.0", CultureInfo.InvariantCulture)} dBmV, past the {DocsisHealthThresholds.UsPowerMarginalDbmv:0} dBmV strain point.",
                    Recommendation = "High upstream TX means the modem is compensating for return-path loss; check for excess attenuation, corrosion, or a failing tap."
                });
        }

        if (parts.Count == 0)
            return new PhysicalLinkResult(NullFactor(factorWeight, "cable modem not reporting RF metrics yet"), issues);

        var totalWeight = parts.Sum(p => p.Weight);
        var score = parts.Sum(p => p.Score * p.Weight) / totalWeight;

        // Channel-loss cap: a sustained drop in locked downstream channels from the window peak.
        if (input.LockedDsChannels is int locked && input.PeakDsChannels is int peak && peak - locked >= 4)
        {
            score = Math.Min(score, 40);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "DOCSIS: downstream channels dropped",
                Description = $"{input.SourceName} locked downstream channels fell from {peak} to {locked}.",
                Recommendation = "Lost channels reduce capacity and signal a marginal plant; check downstream power and SNR."
            });
        }

        var gen = isOfdm ? "DOCSIS 3.1" : "DOCSIS 3.0";
        var desc = $"{gen} cable-modem RF health (MER, FEC, downstream/upstream power)"
                   + (valueBits.Count > 0 ? $": {string.Join(", ", valueBits)}." : ".");
        return new PhysicalLinkResult(
            Factor(factorWeight, (int)Math.Round(score), string.Join(", ", valueBits), desc), issues);
    }

    /// <summary>
    /// Plant generation: an active OFDMA upstream channel (live snapshot) is authoritative;
    /// otherwise a provisioned upstream above the OFDMA-likely line is a strong hint. Absence
    /// of an OFDMA reading is not proof of 3.0 when the plan speed is high.
    /// </summary>
    private static bool IsDocsis31(PhysicalLinkInput input, double? expectedUploadMbps)
    {
        if (input.OfdmaActive == true) return true;
        if (expectedUploadMbps is double up && up > DocsisHealthThresholds.OfdmaLikelyUpstreamMbps) return true;
        return false;
    }

    // ---------------------------------------------------------------------------
    // Cellular
    // ---------------------------------------------------------------------------

    private static PhysicalLinkResult ScoreCellular(PhysicalLinkInput input, double factorWeight)
    {
        var issues = new List<IspHealthIssue>();
        if (input.SignalQuality is not int quality)
            return new PhysicalLinkResult(NullFactor(factorWeight, "cellular modem not reporting signal yet"), issues);

        double score = Math.Clamp(quality, 0, 100);

        if (quality < 25)
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Cellular: poor signal",
                Description = $"{input.SourceName} composite signal quality is {quality}/100.",
                Recommendation = "Reposition the modem or antenna for a stronger signal; weak RF caps throughput and raises latency."
            });

        // A 5G->LTE downgrade only matters on a 5G-capable modem.
        if (input.NetworkModeDowngraded && input.Is5gCapable)
        {
            score = Math.Min(score, score - 5);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Cellular: network downgraded to LTE",
                Description = $"{input.SourceName} dropped from 5G to LTE during the window.",
                Recommendation = "Intermittent 5G coverage reduces peak speeds; antenna placement can help hold 5G."
            });
        }

        var modeBit = string.IsNullOrWhiteSpace(input.NetworkMode) ? "" : $" ({input.NetworkMode})";
        return new PhysicalLinkResult(
            Factor(factorWeight, (int)Math.Round(score), $"Signal {quality}/100{modeBit}",
                "Cellular composite signal quality (RSRP, SNR, RSRQ)."),
            issues);
    }

    // ---------------------------------------------------------------------------
    // Factor helpers
    // ---------------------------------------------------------------------------

    private static IspScoreFactor Factor(double weight, int score, string valueText, string description) => new()
    {
        Name = "Physical Link",
        Score = score,
        Weight = weight,
        ValueText = valueText,
        Description = description
    };

    private static IspScoreFactor NullFactor(double weight, string description) => new()
    {
        Name = "Physical Link",
        Score = null,
        Weight = weight,
        ValueText = null,
        Description = description
    };
}
