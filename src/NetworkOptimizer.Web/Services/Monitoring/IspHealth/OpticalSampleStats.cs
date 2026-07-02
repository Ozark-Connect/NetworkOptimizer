namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Robust receive-power statistics for optical (SFP/ONT) DDM samples, with DDM read-artifact
/// rejection and temperature detrending.
///
/// Two things distort the RX series the Physical Link score rides on:
/// <list type="bullet">
/// <item>A HARD read artifact where temperature jumps tens of degrees in one sample (physically
///   impossible between polls), dragging RX with it. These are rejected outright.</item>
/// <item>A real, repeatable coupling between the DDM temperature reading and the reported RX power
///   (measured at ~0.1 dB per C on a GPON ONT optic, holding on both cooling and warming). This is
///   NOT an optical fault - it is the optic reading a bit lower when it runs cooler - so a temperature
///   swing must not masquerade as a signal dip. We fit the coupling slope from a wide history and
///   DETREND RX to a reference temperature before computing the stats, rather than discarding the
///   samples. A dip steeper than the coupling explains (or one with no temperature move at all)
///   survives detrending and is still seen as a real optical anomaly.</item>
/// </list>
/// Temperature is NOT a health metric here; it only drives artifact rejection and the detrend.
/// </summary>
public static class OpticalSampleStats
{
    /// <summary>
    /// A sample whose temperature deviates from the window median by more than this (C) is treated
    /// as a DDM read artifact and dropped. Real thermal drift is gradual and small between polls;
    /// the hard artifacts seen in the field jump 20-30+ C in one sample, so their temperature can't be
    /// trusted to detrend against either.
    /// </summary>
    public const double DdmTempArtifactDeltaC = 12.0;

    /// <summary>Coupling fit needs at least this many distinct 1 C temperature bins.</summary>
    public const int CouplingMinBins = 3;

    /// <summary>Coupling fit needs the binned temperatures to span at least this many C, or there is
    /// too little leverage to trust a slope (a pinned-temperature window) and detrending is skipped.</summary>
    public const double CouplingMinSpanC = 5.0;

    /// <summary>Theil-Sen only pairs temperature bins at least this far apart, so DDM quantization on a
    /// 1 C step can't dominate the pairwise slopes.</summary>
    public const double CouplingMinPairSpanC = 3.0;

    /// <summary>The fitted coupling slope is clamped to [0, this] dB/C - a physically-plausible band
    /// that bounds the damage from a pathological fit while comfortably covering real optics (~0.1).</summary>
    public const double CouplingMaxSlopeDbmPerC = 0.3;

    /// <summary>Robust RX summary over a window after artifact rejection and temperature detrending.</summary>
    public sealed record RxStats(double? MedianDbm, double? WorstDbm, double? BaselineDbm, int CleanCount, int RejectedArtifacts);

    /// <summary>
    /// Robust least-squares-style slope of RX (dBm) vs temperature (C): Theil-Sen (median pairwise
    /// slope) over per-1 C temperature-bin medians, so DDM quantization and a stray garbage bin can't
    /// dominate. Returns null when the samples lack the temperature spread to fit a trustworthy slope,
    /// and clamps the result to a physically-plausible band. Feed this the WIDE history so a mostly
    /// pinned optic still has the occasional excursion to fit against.
    /// </summary>
    public static double? FitCouplingSlope(IReadOnlyList<(DateTime Time, double? Rx, double? Temp)> samples)
    {
        var byBin = new Dictionary<int, List<double>>();
        foreach (var s in samples)
            if (s.Rx is double rx && s.Temp is double t)
            {
                var bin = (int)Math.Round(t);
                if (!byBin.TryGetValue(bin, out var list)) byBin[bin] = list = new List<double>();
                list.Add(rx);
            }

        var bins = byBin.Select(b => (T: (double)b.Key, Rx: Median(b.Value)!.Value)).OrderBy(b => b.T).ToList();
        if (bins.Count < CouplingMinBins || bins[^1].T - bins[0].T < CouplingMinSpanC) return null;

        var slopes = new List<double>();
        for (var i = 0; i < bins.Count; i++)
            for (var j = i + 1; j < bins.Count; j++)
                if (bins[j].T - bins[i].T >= CouplingMinPairSpanC)
                    slopes.Add((bins[j].Rx - bins[i].Rx) / (bins[j].T - bins[i].T));

        var slope = Median(slopes);
        return slope is double k ? Math.Clamp(k, 0.0, CouplingMaxSlopeDbmPerC) : (double?)null;
    }

    /// <summary>
    /// Computes median / worst / baseline RX over the samples, dropping hard DDM artifacts and then
    /// detrending RX to the window's reference (median) temperature by the supplied coupling slope so
    /// temperature-driven variation doesn't read as an optical dip. Worst is the coldest CLEAN,
    /// detrended sample; baseline is the median of the earliest fifth (for the trend check). Samples
    /// missing RX are ignored; samples missing temperature can't be judged or detrended and pass
    /// through as-is. Pass a null slope (or fit failure) to skip detrending.
    /// </summary>
    public static RxStats Compute(IReadOnlyList<(DateTime Time, double? Rx, double? Temp)> samples, double? couplingSlopeDbmPerC = null)
    {
        var ordered = samples.OrderBy(s => s.Time).ToList();
        var medianTemp = Median(ordered.Where(s => s.Temp.HasValue).Select(s => s.Temp!.Value).ToList());

        var rejected = 0;
        var clean = new List<double>();  // detrended, time-ordered
        foreach (var s in ordered)
        {
            if (s.Rx is not double rx) continue;
            if (medianTemp is double mt && s.Temp is double artT && Math.Abs(artT - mt) > DdmTempArtifactDeltaC)
            {
                rejected++;
                continue;
            }
            // Detrend the reading to the reference temperature using the optic's coupling slope, so a
            // cooler/warmer optic doesn't register as a weaker/stronger link. A reading with no
            // temperature (or no fitted slope) can't be corrected and is used as-is.
            var adjusted = (couplingSlopeDbmPerC is double k && medianTemp is double refT && s.Temp is double t)
                ? rx - k * (t - refT)
                : rx;
            clean.Add(adjusted);
        }

        return new RxStats(Median(clean), clean.Count > 0 ? clean.Min() : null, Baseline(clean), clean.Count, rejected);
    }

    /// <summary>
    /// Robust transmit-power summary for the transmit-power-high rule: the window median (the typical
    /// level, for display) and the highest CLEAN sample (the spike the rule grades), after dropping the
    /// same hard temperature-jump reads that corrupt RX - a lone DDM glitch rides that bad read, so it
    /// can't inflate the spike. TX is NOT temperature-detrended: it is a near-constant laser output set
    /// by ranging, not a margin-graded receive level. Samples missing temperature can't be judged and
    /// are kept; a null spike/median means no TX was reported.
    /// </summary>
    public static (double? MedianDbm, double? SpikeDbm) ComputeTx(IReadOnlyList<(DateTime Time, double? Tx, double? Temp)> samples)
    {
        var medianTemp = Median(samples.Where(s => s.Temp.HasValue).Select(s => s.Temp!.Value).ToList());

        var clean = new List<double>();
        foreach (var s in samples)
        {
            if (s.Tx is not double tx) continue;
            if (medianTemp is double mt && s.Temp is double t && Math.Abs(t - mt) > DdmTempArtifactDeltaC) continue;
            clean.Add(tx);
        }

        return (Median(clean), clean.Count > 0 ? clean.Max() : (double?)null);
    }

    internal static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Median of the earliest fifth of the (time-ordered) clean samples - the trend baseline. Null when too sparse.</summary>
    internal static double? Baseline(List<double> timeOrdered)
    {
        if (timeOrdered.Count < 5) return null;
        var take = Math.Max(1, timeOrdered.Count / 5);
        return Median(timeOrdered.Take(take).ToList());
    }
}
