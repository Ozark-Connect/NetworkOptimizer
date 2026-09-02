using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects co-channel interference where multiple non-mesh APs occupy overlapping
/// spectrum on the same band: the same primary channel, or bonded blocks that share channels
/// (two 320 MHz radios on primaries 5 and 69 share 33 to 61 while never matching on primary).
/// </summary>
public class CoChannelInterferenceRule : IWiFiOptimizerRule
{
    private readonly PropagationService _propagationService;

    public CoChannelInterferenceRule(PropagationService propagationService)
    {
        _propagationService = propagationService;
    }

    public string RuleId => "WIFI-COCHANNEL-001";

    /// <summary>One radio on the band, with the spectrum it occupies.</summary>
    private sealed record BandRadio(AccessPointSnapshot Ap, RadioSnapshot Radio, (int Low, int High) Span)
    {
        public int Channel => Radio.Channel!.Value;
        public int Width => Radio.ChannelWidth ?? 20;
    }

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };

        foreach (var band in bands)
        {
            var radiosInBand = ctx.AccessPoints
                .SelectMany(ap => ap.Radios
                    .Where(r => r.Band == band && r.Channel.HasValue)
                    .Select(r => new BandRadio(ap, r,
                        ChannelSpanHelper.GetChannelSpan(band, r.Channel!.Value, r.ChannelWidth ?? 20, r.CenterChannel))))
                .ToList();

            foreach (var group in GroupByOverlap(radiosInBand))
            {
                // An AP normally has one radio per band; one with two takes its first radio's
                // primary for the mesh and propagation checks and is listed once.
                var channelByMac = new Dictionary<string, int>();
                foreach (var r in group)
                    channelByMac.TryAdd(r.Ap.Mac.ToLowerInvariant(), r.Channel);
                int ChannelOf(AccessPointSnapshot ap) => channelByMac[ap.Mac.ToLowerInvariant()];
                var apsInGroup = group.Select(r => r.Ap).Distinct().ToList();

                // Mesh pairs MUST share a channel, so a group that is nothing but mesh pairs is
                // not an issue. One outsider in the group contends with the pair as much as the
                // pair contends with each other, so then the whole group is reported.
                if (WiFiAnalysisHelpers.FilterOutMeshPairs(apsInGroup, band, ChannelOf).Count == 0) continue;
                var interferingAps = apsInGroup;

                // Spatial filter: remove APs that don't actually interfere with any other AP in the group
                if (ctx.PropagationContext != null && interferingAps.Count > 1)
                {
                    interferingAps = WiFiAnalysisHelpers.FilterByPropagation(
                        interferingAps, band, ChannelOf, ctx.PropagationContext, _propagationService);
                }

                // Only report co-channel if there are 2+ APs left contending
                if (interferingAps.Count <= 1) continue;

                var reported = interferingAps
                    .Select(ap => group.First(r => ReferenceEquals(r.Ap, ap)))
                    .ToList();
                var apNames = reported.Select(r => r.Ap.Name).ToList();
                var primaries = reported.Select(r => r.Channel).Distinct().OrderBy(c => c).ToList();
                var samePrimary = primaries.Count == 1;

                var recommendation = "Consider changing one or more APs to a different channel to reduce interference.";

                // If any APs aren't placed on the map, hint that placing them enables spatial filtering
                var hasUnplacedAps = ctx.PropagationContext == null ||
                    interferingAps.Any(ap => !ctx.PropagationContext.ApsByMac.ContainsKey(ap.Mac.ToLowerInvariant()));
                if (hasUnplacedAps)
                    recommendation += " Place your APs on the Signal Map for more accurate interference analysis based on physical distance and wall attenuation.";

                // Dense deployment check: when APs are placed on the floor plan map, compare
                // the total number of radios on this band against the number of non-overlapping
                // channels available. If there are more APs than channels, some co-channel
                // overlap is structurally unavoidable regardless of channel assignment.
                // Only applies when the floor plan is set up (empirical density signal).
                var nonOverlappingChannels = band switch
                {
                    RadioBand.Band2_4GHz => 3,   // Channels 1, 6, 11
                    RadioBand.Band5GHz => 9,     // UNII-1 + UNII-3 without DFS
                    RadioBand.Band6GHz => 14,    // 80 MHz non-overlapping channels in 6 GHz
                    _ => 3
                };
                var isDenseDeployment = ctx.PropagationContext != null
                    && radiosInBand.Count > nonOverlappingChannels;
                var denseNote = $" With {radiosInBand.Count} APs on {band.ToDisplayString()} and only {nonOverlappingChannels} non-overlapping channels, some overlap is unavoidable.";

                string title, description;
                if (samePrimary)
                {
                    title = $"Co-Channel Interference on {band.ToDisplayString()} Channel {primaries[0]}";
                    description = $"{interferingAps.Count} APs ({string.Join(", ", apNames)}) are using the same channel.";
                }
                else
                {
                    title = $"Co-Channel Interference on {band.ToDisplayString()} Channels {JoinChannels(primaries)}";
                    description = $"{interferingAps.Count} APs are using overlapping spectrum: {DescribeBlocks(reported)}.";
                    var shared = SharedChannels(reported);
                    if (shared != null)
                        description += $" They share channels {shared.Value.Low}-{shared.Value.High}.";
                }
                if (isDenseDeployment)
                    description += denseNote;

                yield return new HealthIssue
                {
                    Severity = isDenseDeployment ? HealthIssueSeverity.Info : HealthIssueSeverity.Warning,
                    Dimensions = { HealthDimension.ChannelHealth },
                    Title = title,
                    Description = description,
                    Recommendation = recommendation,
                    ScoreImpact = isDenseDeployment ? -1 : -5,
                    AffectedChannels = primaries.ToHashSet()
                };
            }
        }
    }

    /// <summary>
    /// Connected groups of radios whose spans overlap, so a chain of partial overlaps is one
    /// issue rather than several naming the same APs. Only groups of two or more are returned.
    /// </summary>
    private static IEnumerable<List<BandRadio>> GroupByOverlap(List<BandRadio> radios)
    {
        var parent = Enumerable.Range(0, radios.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        for (int i = 0; i < radios.Count; i++)
            for (int j = i + 1; j < radios.Count; j++)
                if (ChannelSpanHelper.SpansOverlap(radios[i].Span, radios[j].Span))
                    parent[Find(i)] = Find(j);

        return radios
            .Select((r, i) => (Radio: r, Root: Find(i)))
            .GroupBy(x => x.Root)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.Radio).OrderBy(r => r.Channel).ThenBy(r => r.Ap.Name).ToList());
    }

    /// <summary>The channels every radio in the group occupies, or null when a chain shares none.</summary>
    private static (int Low, int High)? SharedChannels(List<BandRadio> radios)
    {
        var low = radios.Max(r => r.Span.Low);
        var high = radios.Min(r => r.Span.High);
        return low <= high ? (low, high) : null;
    }

    private static string DescribeBlocks(List<BandRadio> radios) =>
        string.Join(", ", radios.Select(r => r.Width > 20
            ? $"{r.Ap.Name} on channel {r.Channel} at {r.Width} MHz ({r.Span.Low}-{r.Span.High})"
            : $"{r.Ap.Name} on channel {r.Channel}"));

    private static string JoinChannels(List<int> channels) => channels.Count switch
    {
        1 => channels[0].ToString(),
        2 => $"{channels[0]} and {channels[1]}",
        _ => string.Join(", ", channels.Take(channels.Count - 1)) + $", and {channels[^1]}"
    };

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Use EvaluateAll for multi-issue rules
        return null;
    }
}
