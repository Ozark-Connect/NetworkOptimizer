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
    /// Active samples a channel needs before it may raise the bar on a move. Lower than the
    /// impetus floor on purpose: declining to move is the cheap error.
    /// </summary>
    public const int MinSamplesForVeto = 200;

    /// <summary>
    /// Active samples a channel needs before it may lower the bar. Moving costs client
    /// disruption and a soak period, so originating one demands more evidence than blocking one.
    /// </summary>
    public const int MinSamplesForImpetus = 500;

    /// <summary>Distinct clients required per channel, so one chatty device cannot speak for a channel.</summary>
    public const int MinDistinctClients = 2;

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
    /// <param name="samples">Per-channel, per-client, per-signal-band aggregates for this AP radio</param>
    /// <param name="currentChannel">Channel the radio is on now</param>
    /// <param name="candidateChannel">Channel the search wants to move it to</param>
    /// <param name="reason">Human-readable justification when the factor is not 1.0</param>
    public static double MoveThresholdFactor(
        IReadOnlyList<ClientRateSample>? samples,
        int currentChannel,
        int candidateChannel,
        out string? reason)
    {
        reason = null;
        if (samples == null || samples.Count == 0 || currentChannel == candidateChannel) return 1.0;

        var current = Summarize(samples, currentChannel);
        var candidate = Summarize(samples, candidateChannel);
        if (current == null || candidate == null) return 1.0;

        // Compare only where the two channels overlap in signal strength. Rate tracks distance
        // hard enough that an unmatched band would report the client mix, not the channel.
        var sharedBands = current.ByBand.Keys.Intersect(candidate.ByBand.Keys).ToList();
        if (sharedBands.Count == 0) return 1.0;

        double curWeighted = 0, candWeighted = 0, weightTotal = 0;
        var sharedSamples = 0;
        foreach (var band in sharedBands)
        {
            var c = current.ByBand[band];
            var k = candidate.ByBand[band];
            // Weight by the thinner side: a band one channel barely visited must not dominate.
            var weight = Math.Min(c.Samples, k.Samples);
            if (weight <= 0) continue;
            curWeighted += c.MeanRateMbps * weight;
            candWeighted += k.MeanRateMbps * weight;
            weightTotal += weight;
            sharedSamples += weight;
        }

        if (weightTotal <= 0 || curWeighted <= 0) return 1.0;

        var currentMean = curWeighted / weightTotal;
        var candidateMean = candWeighted / weightTotal;
        var ratio = candidateMean / currentMean;

        var clientsOk = current.DistinctClients >= MinDistinctClients
                        && candidate.DistinctClients >= MinDistinctClients;
        if (!clientsOk) return 1.0;

        if (ratio >= ImpetusRatio && sharedSamples >= MinSamplesForImpetus)
        {
            reason = $"clients averaged {candidateMean:F0} Mbps on ch {candidateChannel} vs " +
                     $"{currentMean:F0} Mbps on ch {currentChannel} at matched signal " +
                     $"({sharedSamples} samples, {candidate.DistinctClients} clients)";
            return ImpetusFactor;
        }

        if (ratio <= VetoRatio && sharedSamples >= MinSamplesForVeto)
        {
            reason = $"clients averaged {candidateMean:F0} Mbps on ch {candidateChannel} vs " +
                     $"{currentMean:F0} Mbps on ch {currentChannel} at matched signal " +
                     $"({sharedSamples} samples, {candidate.DistinctClients} clients)";
            return VetoFactor;
        }

        return 1.0;
    }

    private sealed class ChannelSummary
    {
        public Dictionary<int, (int Samples, double MeanRateMbps)> ByBand { get; } = new();
        public int DistinctClients { get; set; }
    }

    private static ChannelSummary? Summarize(IReadOnlyList<ClientRateSample> samples, int channel)
    {
        var rows = samples.Where(s => s.Channel == channel && s.SampleCount > 0).ToList();
        if (rows.Count == 0) return null;

        var summary = new ChannelSummary
        {
            DistinctClients = rows.Select(r => r.ClientMac).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        };

        foreach (var group in rows.GroupBy(r => r.SignalBandDbm))
        {
            var n = group.Sum(r => r.SampleCount);
            if (n <= 0) continue;
            var mean = group.Sum(r => r.MeanTxRateMbps * r.SampleCount) / n;
            summary.ByBand[group.Key] = (n, mean);
        }

        return summary.ByBand.Count == 0 ? null : summary;
    }
}
