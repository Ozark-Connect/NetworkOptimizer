using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that flags wide channel widths on 5 GHz and 6 GHz radios.
/// - 6 GHz 320 MHz: always suggest 160 MHz (better client performance + AP co-channel separation)
/// - 5 GHz >= 160 MHz with weak-signal clients: suggest narrowing to 80 MHz
/// - 5 GHz >= 160 MHz where every client is agent-measured and none negotiates more than half the
///   width: the extra width carries nothing (verbiage.md CL-WIDTH)
/// 6 GHz 160 MHz is not flagged (less co-channel interference than 5 GHz).
/// A radio carrying a mesh backhaul is never asked to narrow, on either band: the width is paying
/// for the link. And on a band where any AP carries a backhaul, every other radio's recommendation
/// is per-AP rather than "Apply to All APs", which would narrow the backhaul too. A per-AP width
/// override is reported at Info with a hint rather than as a correction.
/// </summary>
public class WideChannelWidthRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-WIDE-CHANNEL-WIDTH-001";

    private const double WeakClientPctThreshold = 35;
    private const int MinClientsForSignalCheck = 3;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext context)
    {
        var clientsByApBand = context.Clients
            .Where(c => c.IsOnline && c.Signal.HasValue)
            .GroupBy(c => (ApMac: c.ApMac.ToLowerInvariant(), c.Band))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Who carries a backhaul on each band, for the per-AP recommendation.
        var meshApsByBand = new Dictionary<RadioBand, List<string>>();
        foreach (var band in new[] { RadioBand.Band5GHz, RadioBand.Band6GHz })
            meshApsByBand[band] = context.AccessPoints
                .Where(ap => ap.IsOnline && ap.MeshBackhaulUsesBand(band))
                .Select(ap => ap.Name)
                .ToList();

        foreach (var ap in context.AccessPoints)
        {
            foreach (var radio in ap.Radios.Where(r => r.Channel.HasValue))
            {
                if (radio.Band == RadioBand.Band2_4GHz)
                    continue;

                var currentWidth = radio.ChannelWidth ?? 0;
                if (currentWidth < 160)
                    continue;

                // A backhaul radio's width is paying for the link; it is never asked to narrow.
                if (ap.MeshBackhaulUsesBand(radio.Band))
                    continue;

                var key = (ApMac: ap.Mac.ToLowerInvariant(), radio.Band);
                clientsByApBand.TryGetValue(key, out var clients);
                var meshAps = meshApsByBand[radio.Band];

                // Check for weak signal clients
                var hasWeakSignal = false;
                var weakClients = 0;
                var totalClients = clients?.Count ?? 0;
                double weakPct = 0;

                if (clients != null && clients.Count >= MinClientsForSignalCheck)
                {
                    weakClients = clients.Count(c =>
                        SignalClassification.IsWeakSignal(c.Signal!.Value, c.Band));
                    weakPct = (double)weakClients / clients.Count * 100;
                    hasWeakSignal = weakPct >= WeakClientPctThreshold;
                }

                // What the clients negotiate, when the agent measured every one of them.
                int? maxNegotiated = clients != null && clients.Count >= MinClientsForSignalCheck
                    && clients.All(c => c.NegotiatedWidth is > 0)
                    ? clients.Max(c => c.NegotiatedWidth!.Value)
                    : null;
                var unusedWidth = maxNegotiated is { } max && max * 2 <= currentWidth;

                // 6 GHz 320 MHz: always flag (unconditional - better performance + co-channel separation)
                if (radio.Band == RadioBand.Band6GHz && currentWidth >= 320)
                {
                    var issue = hasWeakSignal
                        ? BuildWeakSignalIssue(ap.Mac, ap.Name, radio.Band, currentWidth, 160, weakClients, totalClients, weakPct, meshAps)
                        : BuildInfoIssue(ap.Mac, ap.Name, radio.Band, currentWidth, 160, meshAps);
                    // Copy: verbiage.md CL-WIDTH-NOTE.
                    if (unusedWidth)
                        issue.Description += $" None of its {totalClients} clients negotiate more than {maxNegotiated} MHz today.";
                    yield return WithIntent(issue, radio);
                    continue;
                }

                // 5 GHz >= 160 MHz (160, 240): only flag if weak signal clients, or when the
                // measured negotiation says the width goes unused. 6 GHz 160 MHz is fine - less
                // co-channel interference than 5 GHz.
                if (radio.Band == RadioBand.Band5GHz && currentWidth >= 160)
                {
                    if (hasWeakSignal)
                        yield return WithIntent(
                            BuildWeakSignalIssue(ap.Mac, ap.Name, radio.Band, currentWidth, 80, weakClients, totalClients, weakPct, meshAps), radio);
                    else if (unusedWidth)
                        yield return WithIntent(
                            BuildUnusedWidthIssue(ap.Mac, ap.Name, radio.Band, currentWidth, maxNegotiated!.Value, totalClients, meshAps), radio);
                }
            }
        }
    }

    /// <summary>A width set differently from the band's other radios is a check, not a correction: Info, with the hint.</summary>
    private static HealthIssue WithIntent(HealthIssue issue, RadioSnapshot radio)
    {
        if (radio.WidthIsOverride)
            return RadioIntent.MarkDeliberate(issue, RadioIntent.WidthHint(radio.Band, meshBackhaul: false));
        return issue;
    }

    /// <summary>
    /// Where to change the width. Site-wide through Default WiFi Speeds unless an AP on the band
    /// carries a mesh backhaul, in which case per-AP, naming why (verbiage.md WW-R-MESH).
    /// </summary>
    private static string Recommendation(string apName, RadioBand band, int suggestedWidth, IReadOnlyList<string> meshAps, string siteWideVerb)
    {
        var bandName = band.ToDisplayString();
        if (meshAps.Count == 0)
            return $"In UniFi Network: Settings > WiFi > Default WiFi Speeds > Channel Width - " +
                $"{siteWideVerb} {bandName} to {suggestedWidth} MHz, then Save and Apply to All APs.";

        return $"In UniFi Network: Devices > {apName} > Settings > Radios > {bandName} > Channel Width - " +
            $"set it to {suggestedWidth} MHz on this AP only. Do not use Apply to All APs here: {JoinNames(meshAps)} " +
            $"carry a mesh backhaul on {bandName}, and narrowing them would cut the link's capacity.";
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => string.Join(", ", names.Take(names.Count - 1)) + $", and {names[^1]}"
    };

    /// <summary>
    /// Info-level issue for unconditionally wide channels (6 GHz 320 MHz).
    /// </summary>
    private HealthIssue BuildInfoIssue(string apMac, string apName, RadioBand band, int currentWidth, int suggestedWidth, IReadOnlyList<string> meshAps)
    {
        var bandName = band.ToDisplayString();
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.ChannelHealth },
            Class = HealthIssueClass.Advisory,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(apMac, band)),
            Title = $"{bandName} {currentWidth} MHz: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}. " +
                $"Narrowing to {suggestedWidth} MHz can improve performance on some devices and gives better co-channel separation between APs.",
            AffectedEntity = apName,
            Recommendation = Recommendation(apName, band, suggestedWidth, meshAps, "consider setting"),
            ScoreImpact = -2
        };
    }

    /// <summary>
    /// Warning-level issue when clients have poor signal on wide channels.
    /// </summary>
    private HealthIssue BuildWeakSignalIssue(
        string apMac, string apName, RadioBand band, int currentWidth, int suggestedWidth,
        int weakClients, int totalClients, double weakPct, IReadOnlyList<string> meshAps)
    {
        var bandName = band.ToDisplayString();
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality, HealthDimension.ChannelHealth },
            Class = HealthIssueClass.Advisory,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(apMac, band)),
            Title = $"Wide Channel with Weak Clients on {bandName}: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}, " +
                $"and {weakClients} of {totalClients} clients ({weakPct:F0}%) have weak signal for their band. " +
                $"Wider channels raise the noise floor and reduce effective range. " +
                $"Narrowing to {suggestedWidth} MHz should improve signal quality and reliability.",
            AffectedEntity = apName,
            Recommendation = Recommendation(apName, band, suggestedWidth, meshAps, "set"),
            ScoreImpact = -5
        };
    }

    /// <summary>
    /// Measured branch: every client on the radio is agent-measured and none negotiates more than
    /// half the width. Copy: verbiage.md CL-WIDTH-T / CL-WIDTH-D / CL-WIDTH-R / CL-WIDTH-R-MESH.
    /// </summary>
    private HealthIssue BuildUnusedWidthIssue(
        string apMac, string apName, RadioBand band, int currentWidth, int maxNegotiated, int totalClients, IReadOnlyList<string> meshAps)
    {
        var bandName = band.ToDisplayString();
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.ChannelHealth },
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(apMac, band)),
            Title = $"Unused Width on {bandName}: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}, and none of its {totalClients} clients negotiate more than {maxNegotiated} MHz. " +
                "The extra width is not carrying traffic, and it makes the radio easier to interfere with.",
            AffectedEntity = apName,
            Recommendation = Recommendation(apName, band, maxNegotiated, meshAps, "set"),
            ScoreImpact = -3
        };
    }
}
