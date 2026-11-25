using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects APs with a high percentage of weak-signal clients,
/// indicating coverage gaps near those APs.
/// Uses band-aware thresholds (2.4 GHz needs stronger signal than 6 GHz).
/// </summary>
public class CoverageGapRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-COVERAGE-GAP-001";

    /// <summary>
    /// Minimum clients on an AP to evaluate (avoid flagging APs with few clients).
    /// </summary>
    private const int MinClientsThreshold = 3;

    /// <summary>
    /// Percentage of weak signal clients to trigger this recommendation.
    /// </summary>
    private const double WeakSignalPctThreshold = 40;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var coverageGapAps = new List<(AccessPointSnapshot Ap, int ClientCount, int WeakCount, double WeakPct)>();

        foreach (var ap in ctx.AccessPoints)
        {
            var clientsWithSignal = ctx.Clients
                .Where(c => c.ApMac == ap.Mac && c.Signal.HasValue).ToList();
            if (clientsWithSignal.Count < MinClientsThreshold)
                continue;

            var weakCount = clientsWithSignal.Count(c =>
                SignalClassification.IsWeakSignal(c.Signal!.Value, c.Band));
            var weakPct = (double)weakCount / clientsWithSignal.Count * 100;

            if (weakPct >= WeakSignalPctThreshold)
            {
                coverageGapAps.Add((ap, clientsWithSignal.Count, weakCount, weakPct));
            }
        }

        if (coverageGapAps.Count == 0)
            return null;

        if (coverageGapAps.Count == 1)
        {
            var (ap, clientCount, weakCount, weakPct) = coverageGapAps[0];
            return new HealthIssue
            {
                Severity = HealthIssueSeverity.Warning,
                Dimensions = { HealthDimension.SignalQuality },
                Title = $"Coverage Gap Near {ap.Name}",
                Class = HealthIssueClass.Measured,
                Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Macs(new[] { ap.Mac })),
                Description = $"{weakPct:F0}% of clients ({weakCount} of {clientCount}) connected to {ap.Name} have weak signal " +
                    "for their band. These clients may be too far from the AP or experiencing obstruction.",
                AffectedLinks = { new IssueLink(ap.Name, IssueLinks.Clients(ap.Mac)) },
                AffectedEntity = ap.Name,
                Recommendation = SingleApRecommendation(ap, WeakBands(ctx, ap)),
                ScoreImpact = -8
            };
        }

        // Multiple APs with coverage gaps
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality },
            Title = $"Coverage Gaps Near {coverageGapAps.Count} APs",
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Macs(coverageGapAps.Select(x => x.Ap.Mac))),
            Description = $"{coverageGapAps.Count} access points have >={WeakSignalPctThreshold:F0}% of clients with weak signal for their band. " +
                "This indicates significant coverage gaps in your deployment.",
            Recommendation = MultiApRecommendation(coverageGapAps.Select(x => (x.Ap, WeakBands(ctx, x.Ap))).ToList()),
            ScoreImpact = -8 * coverageGapAps.Count
        }.WithLinks(coverageGapAps.Select(x => new IssueLink($"{x.Ap.Name} ({x.WeakPct:F0}%)", IssueLinks.Clients(x.Ap.Mac))));
    }

    /// <summary>The bands on which this AP's clients are weak: TX power advice is about those radios.</summary>
    private static List<RadioBand> WeakBands(WiFiOptimizerContext ctx, AccessPointSnapshot ap) =>
        ctx.Clients
            .Where(c => c.ApMac == ap.Mac && c.Signal.HasValue && SignalClassification.IsWeakSignal(c.Signal.Value, c.Band))
            .Select(c => c.Band)
            .Distinct()
            .ToList();

    // Steps that cannot help are left out: moving a wall or ceiling AP, and raising TX power
    // that is already at High or its maximum on the weak band.
    internal static string SingleApRecommendation(AccessPointSnapshot ap, IReadOnlyCollection<RadioBand> weakBands)
    {
        var steps = new List<string>();
        if (WiFiAnalysisHelpers.IsMovableAp(ap)) steps.Add("moving this AP closer to weak clients");
        if (WiFiAnalysisHelpers.CanRaiseTxPower(ap, weakBands)) steps.Add("increasing TX power on this AP");
        steps.Add("adding an AP closer to weak clients");
        return Consider(steps);
    }

    internal static string MultiApRecommendation(IReadOnlyList<(AccessPointSnapshot Ap, List<RadioBand> WeakBands)> aps)
    {
        var movable = aps.Where(x => WiFiAnalysisHelpers.IsMovableAp(x.Ap)).Select(x => x.Ap.Name).ToList();
        var canRaise = aps.Where(x => WiFiAnalysisHelpers.CanRaiseTxPower(x.Ap, x.WeakBands)).Select(x => x.Ap.Name).ToList();
        var steps = new List<string>();
        if (movable.Count > 0) steps.Add($"moving {WiFiAnalysisHelpers.JoinNames(movable)} closer to weak clients");
        if (canRaise.Count > 0) steps.Add($"increasing TX power on {WiFiAnalysisHelpers.JoinNames(canRaise)}");
        steps.Add("adding APs in areas with weak coverage");
        return Consider(steps);
    }

    /// <summary>"Consider X." for one step, "Consider: (1) X, (2) Y, or (3) Z." for more.</summary>
    private static string Consider(IReadOnlyList<string> steps)
    {
        if (steps.Count == 1) return $"Consider {steps[0]}.";
        var numbered = steps.Select((s, i) => $"({i + 1}) {s}").ToList();
        return $"Consider: {string.Join(", ", numbered.Take(numbered.Count - 1))}, or {numbered[^1]}.";
    }
}
