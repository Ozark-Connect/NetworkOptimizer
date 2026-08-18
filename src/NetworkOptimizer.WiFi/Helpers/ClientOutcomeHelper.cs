using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Weighs a proposed channel move against what clients actually achieved on the channels
/// involved. The airtime score measures the medium; this measures the experience, and the two
/// can disagree. Pure logic - callers supply already-aggregated samples so the engine keeps no
/// dependency on the telemetry store.
/// </summary>
public static class ClientOutcomeHelper
{
    /// <summary>
    /// Active 15-minute windows a channel needs before it may raise the bar on a move - about five
    /// hours of real traffic. Lower than the impetus floor on purpose: declining to move is the
    /// cheap error.
    /// </summary>
    public const int MinWindowsForVeto = 20;

    /// <summary>
    /// Active windows a channel needs before it may lower the bar, about half a day of real
    /// traffic. Moving costs client disruption and a soak period, so originating a move demands
    /// more evidence than blocking one.
    /// </summary>
    public const int MinWindowsForImpetus = 50;

    /// <summary>
    /// Distinct days a channel's evidence must span. Windows alone can all come from one unusual
    /// evening; days force the comparison to survive more than a single session. This replaces a
    /// distinct-client floor: client_mac is a field rather than a tag, so per-client aggregation
    /// needs a pivot over raw points that measured 33s against 1s for the windowed query.
    /// </summary>
    public const int MinDistinctDays = 3;

    /// <summary>Candidate must beat the current channel by this much before it lowers the bar.</summary>
    public const double ImpetusRatio = 1.15;

    /// <summary>At or below this the candidate measured worse than where we already are.</summary>
    public const double VetoRatio = 0.95;

    /// <summary>Bar multiplier when client history backs the move.</summary>
    public const double ImpetusFactor = 0.7;

    /// <summary>Bar multiplier when client history contradicts it.</summary>
    public const double VetoFactor = 1.6;

    /// <summary>
    /// How far back client telemetry is read. Matched to the fast bucket's 90-day retention -
    /// asking for more returns nothing extra and just widens the query.
    /// </summary>
    public static readonly TimeSpan ClientRateWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// Multiplier for the "is this AP suffering enough to move?" threshold, from what clients
    /// measured on the current channel versus the candidate. Returns 1.0 - changing nothing -
    /// whenever the evidence is missing, thin, or equivocal, which is the common case.
    /// </summary>
    /// <param name="samples">Per-channel, per-signal-band, per-day aggregates for this AP radio</param>
    /// <param name="currentChannel">Channel the radio is on now</param>
    /// <param name="candidateChannel">Channel the search wants to move it to</param>
    /// <param name="reason">Why the factor came out as it did, for the recommendation log</param>
    public static double MoveThresholdFactor(
        IReadOnlyList<ClientRateSample>? samples,
        int currentChannel,
        int candidateChannel,
        out string? reason)
    {
        reason = null;
        if (samples == null || samples.Count == 0)
        {
            reason = "no client history for this radio";
            return 1.0;
        }
        if (currentChannel == candidateChannel) return 1.0;

        var current = Summarize(samples, currentChannel);
        var candidate = Summarize(samples, candidateChannel);
        if (current == null || candidate == null)
        {
            reason = current == null
                ? $"no client history on current ch {currentChannel}"
                : $"no client history on candidate ch {candidateChannel}";
            return 1.0;
        }

        // Compare only where the two channels overlap in signal strength. Rate tracks distance
        // hard enough that an unmatched band would report the client mix, not the channel.
        var sharedBands = current.ByBand.Keys.Intersect(candidate.ByBand.Keys).ToList();
        if (sharedBands.Count == 0)
        {
            reason = "no overlapping signal bands between the two channels";
            return 1.0;
        }

        double curWeighted = 0, candWeighted = 0;
        var sharedWindows = 0;
        foreach (var band in sharedBands)
        {
            var c = current.ByBand[band];
            var k = candidate.ByBand[band];
            // Weight by the thinner side: a band one channel barely visited must not dominate.
            var weight = Math.Min(c.Windows, k.Windows);
            if (weight <= 0) continue;
            curWeighted += c.MeanRateMbps * weight;
            candWeighted += k.MeanRateMbps * weight;
            sharedWindows += weight;
        }

        if (sharedWindows <= 0 || curWeighted <= 0)
        {
            reason = "no comparable windows in the shared signal bands";
            return 1.0;
        }

        var currentMean = curWeighted / sharedWindows;
        var candidateMean = candWeighted / sharedWindows;
        var ratio = candidateMean / currentMean;

        if (current.DistinctDays < MinDistinctDays || candidate.DistinctDays < MinDistinctDays)
        {
            reason = $"evidence spans too few days (current {current.DistinctDays}, " +
                     $"candidate {candidate.DistinctDays}, need {MinDistinctDays})";
            return 1.0;
        }

        var summary = $"clients averaged {candidateMean:F0} Mbps on ch {candidateChannel} vs " +
                      $"{currentMean:F0} Mbps on ch {currentChannel} at matched signal " +
                      $"({sharedWindows} shared windows over {candidate.DistinctDays} days)";

        if (ratio >= ImpetusRatio && sharedWindows >= MinWindowsForImpetus)
        {
            reason = summary;
            return ImpetusFactor;
        }

        if (ratio <= VetoRatio && sharedWindows >= MinWindowsForVeto)
        {
            reason = summary;
            return VetoFactor;
        }

        reason = $"inconclusive: ratio {ratio:F2} over {sharedWindows} shared windows";
        return 1.0;
    }

    private sealed class ChannelSummary
    {
        public Dictionary<int, (int Windows, double MeanRateMbps)> ByBand { get; } = new();
        public int DistinctDays { get; set; }
    }

    private static ChannelSummary? Summarize(IReadOnlyList<ClientRateSample> samples, int channel)
    {
        var rows = samples.Where(s => s.Channel == channel && s.WindowCount > 0).ToList();
        if (rows.Count == 0) return null;

        var summary = new ChannelSummary
        {
            DistinctDays = rows.Select(r => r.Day.Date).Distinct().Count()
        };

        foreach (var group in rows.GroupBy(r => r.SignalBandDbm))
        {
            var n = group.Sum(r => r.WindowCount);
            if (n <= 0) continue;
            var mean = group.Sum(r => r.MeanTxRateMbps * r.WindowCount) / n;
            summary.ByBand[group.Key] = (n, mean);
        }

        return summary.ByBand.Count == 0 ? null : summary;
    }
}
