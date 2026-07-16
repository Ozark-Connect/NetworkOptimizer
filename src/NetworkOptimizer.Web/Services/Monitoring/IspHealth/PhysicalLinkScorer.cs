using System.Globalization;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Pure scorer for the Access Layer "Physical Link" factor. Given window-aggregated
/// metrics for the one source matched to the WAN, produces a 0-100 sub-score plus any
/// issues. No I/O; fully unit-testable.
///
/// Design (see research/isp-health/physical-link-access-scoring.md):
/// - PON/AE grade on ABSOLUTE receive power margin-to-floor (a gentle healthy slope so
///   more insertion loss reads slightly lower, never a flat 100), scored on the robust median
///   after DDM read-artifact rejection (see OpticalSampleStats), with a worst-sustained cap and
///   hard caps for a down PON link or hot TX. Temperature is NOT a health input here (the
///   monitoring system alerts on optic temp); it only drives artifact rejection. The inferred
///   splitter ratio is DISPLAY-ONLY verbiage derived from the optical power budget and never feeds
///   the score. A reading colder than the coldest any realistic 1:64 split can produce is a
///   bounded, baseline-free excess-loss flag.
/// - DOCSIS grades MER, FEC (a 50/50 blend of the uncorrectable ratio and the per-day
///   uncorrectable rate, the rate scaled up for 3.1 OFDM's higher codeword volume), and DS/US
///   power, with a channel-loss cap. Plant generation is inferred from an active OFDMA channel,
///   else plan speed.
/// - Cellular passes the existing composite signal quality through.
/// Reuses <see cref="PonThresholds"/> and <see cref="DocsisHealthThresholds"/> as the
/// single source-of-truth for breach anchors.
/// </summary>
public static class PhysicalLinkScorer
{
    /// <summary>An RX drop of at least this many dB from the link's own baseline is developing loss.</summary>
    private const double PonTrendDropDbm = 2.5;

    /// <summary>Standard PON splitter rungs (ratio, typical insertion loss dB incl. excess), capped at 1:64.</summary>
    private static readonly (int Ratio, double LossDb)[] SplitterRungs =
    {
        (2, 3.6), (4, 7.2), (8, 10.5), (16, 13.8), (32, 17.1), (64, 20.4)
    };

    /// <summary>Generic note appended to a factor description when a TRANSIENT event the snapshot
    /// values don't show (a dip, an interruption/break, a developing drop, or an error burst) was
    /// factored into the score. Steady-state conditions (marginal/hot power, low SNR, poor signal)
    /// are visible in the displayed values, so they do NOT get this note.</summary>
    private const string AnomalyNote = " A signal anomaly or interruption during this window was factored into the score.";

    public static PhysicalLinkResult Score(PhysicalLinkInput input, double? expectedUploadMbps, double factorWeight, ILogger? logger = null)
    {
        return input.Medium switch
        {
            PhysicalMedium.Pon => ScoreOptical(input, factorWeight, isPon: true, logger),
            PhysicalMedium.ActiveEthernet => ScoreOptical(input, factorWeight, isPon: false, logger),
            PhysicalMedium.Docsis => ScoreDocsis(input, expectedUploadMbps, factorWeight),
            PhysicalMedium.Cellular => ScoreCellular(input, factorWeight),
            PhysicalMedium.Satellite => ScoreSatellite(input, factorWeight),
            _ => new PhysicalLinkResult(NullFactor(factorWeight, "no usable physical-link data"), new())
        };
    }

    // ---------------------------------------------------------------------------
    // Optical: PON and Active Ethernet
    // ---------------------------------------------------------------------------

    private static PhysicalLinkResult ScoreOptical(PhysicalLinkInput input, double factorWeight, bool isPon, ILogger? logger)
    {
        var issues = new List<IspHealthIssue>();
        var rx = input.RxPowerMedianDbm;
        // The configured access technology drives the copy so it reads "GPON"/"XGS-PON"
        // consistently with the user's selection, not whatever the ONT happens to report.
        var label = isPon
            ? (input.IsXgsPon ? "XGS-PON" : "GPON")
            : "Active Ethernet";

        if (rx is null)
            return new PhysicalLinkResult(NullFactor(factorWeight, $"{label} link not reporting optical power yet"), issues);

        // The score curve rides PON receiver PHYSICS (marginal/floor), via the PonThresholds
        // constants - deliberately NOT the user's SfpDdmThresholds alert override, which tunes when
        // the SFP DDM alarm fires, not how ISP Health grades optical health. A tightened alert must
        // not compress this curve. Gentle healthy slope, knee at the marginal anchor, zero at the
        // receiver sensitivity floor. Warmer = healthier until overload.
        var rxLow = isPon ? PonThresholds.PonRxPowerLowDbm : PonThresholds.AeRxPowerLowDbm;
        var floor = isPon ? PonThresholds.PonRxFloorDbm : PonThresholds.AeRxFloorDbm;        // receiver sensitivity
        var overload = isPon ? PonThresholds.PonRxOverloadDbm : PonThresholds.AeRxOverloadDbm;  // too hot
        var healthyTop = isPon ? -24.0 : -13.0;        // top of the gentle in-spec slope (scoring-curve shape)

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
            var split = InferSplitRatio(rx.Value, input.IsXgsPon);
            if (split != null) detailBits.Add(split);

            if (rx.Value < PonThresholds.PonExcessLossFloorDbm && rx.Value > floor)
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

        // Caps and secondary signals. PonOperational reflects the O5 state across the WINDOW (from the
        // series, not a single live poll), so a break may have happened earlier and since recovered -
        // the copy is past/window tense, not "is down right now".
        if (input.PonOperational == false)
        {
            score = Math.Min(score, 10);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Critical,
                Title = $"{label}: link dropped out of O5",
                Description = $"{input.SourceName} PON link left the Operation (O5) state at least once during this window - the optical link went down or re-ranged.",
                Recommendation = "A single brief drop can be a re-range; repeated O5 breaks point to a fiber or ONT fault - inspect the ONT and the optical path."
            });
        }

        var txHigh = isPon
            ? (input.IsXgsPon ? PonThresholds.XgsPonTxPowerHighDbm : PonThresholds.PonTxPowerHighDbm)
            : PonThresholds.AeTxPowerHighDbm;
        // Grade the TX SPIKE (highest clean sample), the transmit counterpart of the RX worst - a real
        // or sustained hot laser trips it, a lone glitch (already artifact-rejected) can't. "Peaked at"
        // when only the spike is over; "is" when the typical (median) level is over too, so the copy
        // matches what the displayed median TX shows.
        if (input.TxPowerSpikeDbm is double txSpike && txSpike > txHigh)
        {
            score = Math.Min(score, 75);
            var sustained = input.TxPowerDbm is double txMed && txMed > txHigh;
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = $"{label}: transmit power high",
                Description = $"{input.SourceName} transmit optical power {(sustained ? "is" : "peaked at")} {txSpike.ToString("0.0", CultureInfo.InvariantCulture)} dBm, above the {txHigh:0} dBm threshold.",
                Recommendation = "A consistently high TX can indicate the laser is compensating for path loss; inspect the optical path."
            });
        }

        // FEC/BIP error corroboration (external ONT only; SFP DDM reports none). A healthy PON link
        // logs a negligible BIP / uncorrectable-FEC count, so these are graded as an absolute count
        // over the window normalized to a per-day rate (BIP and FEC sharing the same healthy line).
        // A climbing count caps the optical score - real excess loss leaves error fingerprints,
        // rather than inferring it circularly from RX-vs-split.
        var fecPerDay = input.WindowDays > 0 && input.FecErrorsTotal is long fecT ? fecT / input.WindowDays : (double?)null;
        var bipPerDay = input.WindowDays > 0 && input.BipErrorsTotal is long bipT ? bipT / input.WindowDays : (double?)null;
        var errorScore = 100.0;
        if (fecPerDay is double fpd) errorScore = Math.Min(errorScore, PonErrorPerDayScore(fpd));
        if (bipPerDay is double bpd) errorScore = Math.Min(errorScore, PonErrorPerDayScore(bpd));
        score = Math.Min(score, errorScore);
        var errorsHigh = (fecPerDay is double f1 && f1 > PonThresholds.PonErrorsPerDayPoor)
                         || (bipPerDay is double b1 && b1 > PonThresholds.PonErrorsPerDayPoor);
        if (errorsHigh)
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = $"{label}: optical errors accumulating",
                Description = $"{input.SourceName} is logging optical errors"
                              + (fecPerDay is double f and > 0 ? $" (~{f.ToString("0", CultureInfo.InvariantCulture)}/day uncorrectable FEC)" : "")
                              + (bipPerDay is double b and > 0 ? $" (~{b.ToString("0", CultureInfo.InvariantCulture)}/day BIP)" : "")
                              + " - a healthy link sees a negligible count.",
                Recommendation = "Inspect the drop for excess loss - a dirty/loose connector, a macrobend, or a degrading splice."
            });

        // Temperature is intentionally NOT a health input or display detail here - the monitoring
        // system already alerts on optic temperature. Its only role for the Physical Link factor is
        // upstream DDM read-artifact rejection (see OpticalSampleStats).

        var finalScore = (int)Math.Round(score);
        // Show TX alongside RX now that TX participates in scoring.
        var valueText = input.TxPowerDbm is double txp
            ? $"{rxText}, {txp.ToString("0.0", CultureInfo.InvariantCulture)} dBm TX"
            : rxText;
        // Note only TRANSIENT events the displayed RX/TX don't reveal: a receive-power dip (worst
        // sample >0.5 dB below the median, where the worst-cap engaged - artifacts are already
        // discarded so this is genuine), a transmit-power spike (highest sample over the threshold
        // while the displayed median TX is not - a sustained hot TX shows in the value instead), a link
        // interruption (not in O5), a developing drop (trend vs baseline), or an FEC/BIP error burst.
        // Steady marginal/hot RX or TX show in the values.
        var txSpikeTransient = input.TxPowerSpikeDbm is double ts && ts > txHigh
                               && !(input.TxPowerDbm is double tm && tm > txHigh);
        var transientAnomaly =
            (input.RxPowerWorstDbm is double worstDip && rx.Value - worstDip > 0.5)
            || input.PonOperational == false
            || (isPon && input.RxPowerBaselineDbm is double baseDrop && rx.Value <= baseDrop - PonTrendDropDbm)
            || errorsHigh
            || txSpikeTransient;
        var desc = $"{label} optical receive power scored on margin to the receiver floor"
                   + (detailBits.Count > 0 ? $" ({string.Join(", ", detailBits)})." : ".")
                   + (transientAnomaly ? AnomalyNote : "");

        logger?.LogDebug(
            "ISP Health physical(optical {Label}): '{Source}' rxMed={Rx} -> {Score} (worst={Worst}, baseline={Base}, op={Op}, tx={Tx}, fecScore={ErrScore}, {Detail})",
            label, input.SourceName, FormatOrNull(rx), finalScore, FormatOrNull(input.RxPowerWorstDbm),
            FormatOrNull(input.RxPowerBaselineDbm), input.PonOperational, FormatOrNull(input.TxPowerDbm),
            (int)Math.Round(errorScore), detailBits.Count > 0 ? string.Join(", ", detailBits) : "n/a");

        return new PhysicalLinkResult(Factor(factorWeight, finalScore, valueText, desc), issues);
    }

    private static string FormatOrNull(double? v) => v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>0-100 score for a PON per-day BIP/uncorrectable-FEC count: 100 at 0 (ideal), still
    /// high through the good line (~few/day), poor past the poor line. BIP and FEC share this curve.</summary>
    private static double PonErrorPerDayScore(double perDay) => ScoreCurve.Interpolate(perDay,
        (0, 100), (PonThresholds.PonErrorsPerDayGood, 92),
        (PonThresholds.PonErrorsPerDayPoor, 25), (PonThresholds.PonErrorsPerDayPoor * 4, 0));

    /// <summary>
    /// Inferred EFFECTIVE split ratio for display only, derived from the optical power budget:
    ///   RX = OLT_launch - splitter_loss - distribution_loss
    ///   =&gt; splitter_loss = OLT_launch - RX - distribution_loss   (snap to nearest standard rung)
    /// The launch and distribution-loss constants are realistic typicals (common GPON B+ launch
    /// and an average residential drop incl. nominal glass, splices, and un-cleaned connectors);
    /// high-class OLTs are rare and XGS-PON launches hotter. These are starting defaults meant to
    /// be nudged from field feedback - the model is the budget math, not a hand-tuned table. Never
    /// feeds the score. Returns null when the math lands hotter than even a 1:2 split.
    /// </summary>
    internal static string? InferSplitRatio(double rxDbm, bool isXgs)
    {
        var oltLaunchDbm = isXgs ? 5.0 : 3.0;     // common launch (GPON B+; high-class C+ is the rare rural case)
        const double distributionLossDb = 5.0;    // realistic drop: glass + splices + typical un-cleaned connectors
                                                  // (puts the 1:32 -> 1:64 boundary near -20.75 dBm on GPON)
        var splitterLoss = oltLaunchDbm - rxDbm - distributionLossDb;

        // Colder than the deepest realistic 1:64 can produce is excess loss, not a bigger splitter.
        if (rxDbm <= PonThresholds.PonExcessLossFloorDbm) return "est. 1:64+ split or excess loss";
        if (splitterLoss < SplitterRungs[0].LossDb - 2.0) return null;  // too hot for even 1:2

        var best = SplitterRungs[0];
        foreach (var rung in SplitterRungs)
            if (Math.Abs(rung.LossDb - splitterLoss) <= Math.Abs(best.LossDb - splitterLoss))
                best = rung;

        return $"est. 1:{best.Ratio} split";
    }

    // ---------------------------------------------------------------------------
    // DOCSIS
    // ---------------------------------------------------------------------------

    private static PhysicalLinkResult ScoreDocsis(PhysicalLinkInput input, double? expectedUploadMbps, double factorWeight)
    {
        var issues = new List<IspHealthIssue>();
        var isOfdm = IsDocsis31(input, expectedUploadMbps);
        // Transient events only (channel loss, uncorrectable error burst). Steady power/SNR
        // conditions are visible in the displayed values, so they don't get the anomaly note.
        var transientAnomaly = false;

        var parts = new List<(double Score, double Weight)>();
        var valueBits = new List<string>();   // headline (ValueText): SNR + US, the key factors
        var detailBits = new List<string>();  // description: the other stats (DS power, FEC)

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
                    Recommendation = "Low SNR drives uncorrectable errors. Check your coax, connectors, and any unused splitters first; if those are clean, the cause is usually plant-side (ingress, a failing amplifier, or the node) and needs a line tech."
                });
        }

        // FEC sub-score = 50% RATIO + 50% RATE. The ratio unc/(corr+unc) is "how dominant are
        // uncorrectables among errored codewords" (texture, gen-independent); the rate is
        // uncorrectables/day (the magnitude gate, scaled up for 3.1's higher codeword volume). A
        // moderate ratio with a low rate is normal - only a high rate (or a dominant ratio) drags it.
        if (input.CorrectablesDelta is long corr && input.UncorrectablesDelta is long unc)
        {
            var denom = corr + unc;
            var ratio = denom > 0 ? (double)unc / denom : 0.0;
            var ratioScore = denom <= 0 ? 100.0 : ScoreCurve.Interpolate(ratio,
                (0, 100), (DocsisHealthThresholds.FecUncorrRatioGood, 90), (DocsisHealthThresholds.FecUncorrRatioPoor, 0));

            var rateGood = isOfdm ? DocsisHealthThresholds.UncorrPerDayGoodOfdm : DocsisHealthThresholds.UncorrPerDayGoodScQam;
            var ratePoor = isOfdm ? DocsisHealthThresholds.UncorrPerDayPoorOfdm : DocsisHealthThresholds.UncorrPerDayPoorScQam;
            var uncPerDay = input.WindowDays > 0 ? unc / input.WindowDays : 0.0;
            var rateScore = ScoreCurve.Interpolate(uncPerDay, (0, 100), (rateGood, 92), (ratePoor, 25), (ratePoor * 10, 0));

            parts.Add((0.5 * ratioScore + 0.5 * rateScore, 0.30));
            detailBits.Add(unc == 0
                ? "FEC clean"
                : $"FEC {(ratio * 100).ToString("0.#", CultureInfo.InvariantCulture)}% uncorr (~{uncPerDay.ToString("0", CultureInfo.InvariantCulture)}/day)");

            // Issue when uncorrectables dominate the errored codewords (>40%) or the rate is clearly high.
            if (unc > 0 && (ratio > DocsisHealthThresholds.FecUncorrRatioIssue || uncPerDay >= ratePoor))
            {
                transientAnomaly = true;
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "DOCSIS: uncorrectable errors",
                    Description = $"{input.SourceName} uncorrectable codewords are {(ratio * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of errored codewords"
                                  + (uncPerDay > 0 ? $" (~{uncPerDay.ToString("0", CultureInfo.InvariantCulture)}/day)" : "") + ".",
                    Recommendation = "Sustained uncorrectables mean data loss; check downstream SNR and the coax/connectors for ingress."
                });
            }
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
            // DS power: scored above (tent near 0 dBmV) and shown in the description, off the headline.
            detailBits.Add($"DS {dsp.ToString("0.0", CultureInfo.InvariantCulture)} dBmV");
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
                    Recommendation = "Eliminate any unused splitters; if that doesn't help, a line tech may need to check your drop, the amplifier, or the node."
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
            transientAnomaly = true;
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "DOCSIS: downstream channels dropped",
                Description = $"{input.SourceName} locked downstream channels fell from {peak} to {locked}.",
                Recommendation = "Lost channels reduce capacity and signal a marginal plant; check downstream power and SNR."
            });
        }

        // An active OFDMA channel proves an OFDM-capable plant but can't tell 3.1 from 4.0
        // (4.0 also runs OFDMA), so we name both rather than claim 3.1 specifically.
        var gen = isOfdm ? "DOCSIS 3.1/4.0" : "DOCSIS 3.0";
        // Headline (ValueText) carries SNR + US; the description lists the other scored stats (DS, FEC).
        var desc = $"{gen} cable-modem RF health"
                   + (detailBits.Count > 0 ? $" ({string.Join(", ", detailBits)})." : ".")
                   + (transientAnomaly ? AnomalyNote : "");
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
            score = Math.Max(0, score - 5);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Cellular: network downgraded to LTE",
                Description = $"{input.SourceName} dropped from 5G to LTE during the window.",
                Recommendation = "Intermittent 5G coverage reduces peak speeds; antenna placement can help hold 5G."
            });
        }

        var modeBit = string.IsNullOrWhiteSpace(input.NetworkMode) ? "" : $" ({input.NetworkMode})";
        // Only the 5G->LTE downgrade is a transient event; a steady poor signal shows in the value.
        var desc = "Cellular composite signal quality (RSRP, SNR, RSRQ)."
                   + (input.NetworkModeDowngraded && input.Is5gCapable ? AnomalyNote : "");
        return new PhysicalLinkResult(
            Factor(factorWeight, (int)Math.Round(score), $"Signal {quality}/100{modeBit}", desc),
            issues);
    }

    // ---------------------------------------------------------------------------
    // Satellite (Starlink)
    // ---------------------------------------------------------------------------

    /// <summary>Dish alerts that cap the score when raised (live snapshot): name -> (cap, severity, copy).</summary>
    private static readonly (string Alert, int Cap, IspIssueSeverity Severity, string Title, string Recommendation)[] SatelliteCapAlerts =
    {
        ("thermal_shutdown", 10, IspIssueSeverity.Critical, "Starlink: dish in thermal shutdown",
            "The dish has shut down its radio from heat. Shade the dish or improve airflow; service is down until it cools."),
        ("thermal_throttle", 50, IspIssueSeverity.Warning, "Starlink: dish thermally throttled",
            "The dish is reducing performance from heat. Shade the dish or improve airflow."),
        ("power_supply_thermal_throttle", 50, IspIssueSeverity.Warning, "Starlink: power supply thermally throttled",
            "The power supply is overheating; move it somewhere cooler or improve airflow."),
        ("mast_not_near_vertical", 70, IspIssueSeverity.Warning, "Starlink: mast is not vertical",
            "A tilted mount degrades tracking. Level the mount so the dish can align properly."),
        ("dish_water_detected", 60, IspIssueSeverity.Warning, "Starlink: water detected in the dish",
            "The dish is reporting water intrusion; inspect the unit and its cable connections."),
        ("lower_signal_than_predicted", 75, IspIssueSeverity.Warning, "Starlink: signal lower than predicted",
            "The dish sees weaker signal than its ephemeris predicts, which usually means partial blockage or misalignment."),
    };

    private static PhysicalLinkResult ScoreSatellite(PhysicalLinkInput input, double factorWeight)
    {
        var issues = new List<IspHealthIssue>();
        var parts = new List<(double Score, double Weight)>();
        var valueBits = new List<string>();
        var detailBits = new List<string>();
        var transientAnomaly = false;

        // Sky obstruction: the defining Starlink plant metric. Fraction of sky
        // time obstructed, graded on the dish's own reporting conventions.
        if (input.ObstructionFraction is double obstructed)
        {
            var obstructionScore = ScoreCurve.Interpolate(obstructed,
                (0, 100),
                (StarlinkHealthThresholds.ObstructionFractionGood, 92),
                (StarlinkHealthThresholds.ObstructionFractionPoor, 40),
                (StarlinkHealthThresholds.ObstructionFractionCritical, 0));
            parts.Add((obstructionScore, 0.40));
            valueBits.Add($"{(obstructed * 100).ToString("0.##", CultureInfo.InvariantCulture)}% obstructed");

            if (obstructed >= StarlinkHealthThresholds.ObstructionFractionPoor)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Starlink: sky obstruction",
                    Description = $"{input.SourceName} is obstructed {(obstructed * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of the time - enough to cause regular interruptions.",
                    Recommendation = "Check the obstruction map for the blocked direction and relocate the dish or clear the obstruction (trees are the usual cause)."
                });
        }

        // Dish-to-ground loss from the dish's own 1 Hz ping history - loss the
        // dish sees on the satellite path itself, upstream of anything local.
        if (input.DishDropRateAvg is double drop)
        {
            var dropScore = ScoreCurve.Interpolate(drop,
                (0, 100),
                (StarlinkHealthThresholds.DropRateGood, 90),
                (0.01, 60),
                (StarlinkHealthThresholds.DropRatePoor, 20),
                (0.10, 0));
            parts.Add((dropScore, 0.35));
            valueBits.Add($"{(drop * 100).ToString("0.##", CultureInfo.InvariantCulture)}% loss");

            if (drop >= StarlinkHealthThresholds.DropRatePoor)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Starlink: high dish-side packet loss",
                    Description = $"{input.SourceName} is dropping {(drop * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of its own pings to the ground station.",
                    Recommendation = "Sustained dish-side loss points to obstruction, weather, or cell congestion rather than anything on your LAN."
                });
        }

        // Outage burden from the dish outage log, normalized to seconds/day.
        if (input.OutageSecondsTotal is double outageS && input.WindowDays > 0)
        {
            var perDay = outageS / input.WindowDays;
            var outageScore = ScoreCurve.Interpolate(perDay,
                (0, 100),
                (StarlinkHealthThresholds.OutageSecondsPerDayGood, 90),
                (60, 60),
                (StarlinkHealthThresholds.OutageSecondsPerDayPoor, 25),
                (StarlinkHealthThresholds.OutageSecondsPerDayPoor * 4, 0));
            parts.Add((outageScore, 0.25));
            if (perDay > 0)
            {
                detailBits.Add($"~{perDay.ToString("0", CultureInfo.InvariantCulture)} s/day outage"
                               + (input.OutageCountTotal is long oc and > 0 ? $" across {oc} event{(oc == 1 ? "" : "s")}" : ""));
                transientAnomaly = true;
            }

            if (perDay >= StarlinkHealthThresholds.OutageSecondsPerDayPoor)
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Starlink: frequent outages",
                    Description = $"{input.SourceName} logged ~{perDay.ToString("0", CultureInfo.InvariantCulture)} seconds of outage per day over this window.",
                    Recommendation = "Check the outage causes on the Starlink Stats tab - obstruction outages need a clearer sky view; NO_SATS/NO_DOWNLINK bursts are usually constellation or weather."
                });
        }

        if (parts.Count == 0)
            return new PhysicalLinkResult(NullFactor(factorWeight, "Starlink terminal not reporting health data yet"), issues);

        var totalWeight = parts.Sum(p => p.Weight);
        var score = parts.Sum(p => p.Score * p.Weight) / totalWeight;

        // Persistently low SNR is the dish's own "signal is degraded" verdict.
        if (input.SnrPersistentlyLow == true)
        {
            score = Math.Min(score, 40);
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Starlink: persistently low SNR",
                Description = $"{input.SourceName} reports its signal-to-noise ratio has been persistently low.",
                Recommendation = "Check for partial obstructions or snow/debris on the dish."
            });
        }

        // Alert-driven caps (thermal, tilt, water, low signal).
        if (input.DishAlerts is { Count: > 0 } alerts)
        {
            foreach (var (alert, cap, severity, title, recommendation) in SatelliteCapAlerts)
            {
                if (!alerts.Contains(alert, StringComparer.OrdinalIgnoreCase)) continue;
                score = Math.Min(score, cap);
                issues.Add(new IspHealthIssue
                {
                    Severity = severity,
                    Title = title,
                    Description = $"{input.SourceName} is raising the {alert.Replace('_', ' ')} alert.",
                    Recommendation = recommendation
                });
            }
        }

        // Slow negotiated Ethernet is LAN-side, not the ISP - advisory only, no cap,
        // but it silently bottlenecks every measurement downstream of the dish.
        if (input.EthSpeedMbps is int eth and > 0 and < 1000)
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Starlink: Ethernet negotiated below 1 Gbps",
                Description = $"{input.SourceName} negotiated {eth} Mbps to the router, which caps throughput below what the dish can deliver.",
                Recommendation = "Check the cable and connectors between the dish/power supply and the router; a damaged run commonly negotiates 100 Mbps."
            });

        var finalScore = (int)Math.Round(score);
        var desc = "Starlink dish health scored on sky obstruction, dish-to-ground loss, and outage burden"
                   + (detailBits.Count > 0 ? $" ({string.Join(", ", detailBits)})." : ".")
                   + (transientAnomaly ? AnomalyNote : "");
        var valueText = valueBits.Count > 0 ? string.Join(", ", valueBits) : "dish reporting";
        return new PhysicalLinkResult(
            Factor(factorWeight, finalScore, valueText, desc), issues);
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
