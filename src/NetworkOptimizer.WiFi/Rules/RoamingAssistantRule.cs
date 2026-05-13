using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

public class RoamingAssistantRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-ROAMING-ASSISTANT-001";

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        if (ctx.AccessPoints.Count <= 1)
            return null;

        // Try SSID-level settings first (newer UniFi Network versions)
        var enabledWlans = ctx.Wlans.Where(w => w.Enabled).ToList();
        var hasSsidLevelSettings = enabledWlans.Any(w => w.RoamingAssistant5GHzEnabled.HasValue);

        if (hasSsidLevelSettings)
            return EvaluateSsidLevel(ctx, enabledWlans);

        // Fallback: per-AP radio_table settings (older UniFi Network versions)
        return EvaluatePerAp(ctx);
    }

    private static HealthIssue? EvaluateSsidLevel(WiFiOptimizerContext ctx, List<WlanConfiguration> enabledWlans)
    {
        var issues = new List<string>();

        if (ctx.Has5GHzCoverage)
        {
            var wlansWithout5g = enabledWlans
                .Where(w => w.EnabledBands.Contains(RadioBand.Band5GHz) &&
                            w.RoamingAssistant5GHzEnabled != true)
                .ToList();

            if (wlansWithout5g.Count > 0)
                issues.Add($"5 GHz: {string.Join(", ", wlansWithout5g.Select(w => w.Name))}");
        }

        if (ctx.Has6GHzCoverage)
        {
            var wlansWithout6g = enabledWlans
                .Where(w => w.EnabledBands.Contains(RadioBand.Band6GHz) &&
                            w.RoamingAssistant6GHzEnabled != true)
                .ToList();

            if (wlansWithout6g.Count > 0)
                issues.Add($"6 GHz: {string.Join(", ", wlansWithout6g.Select(w => w.Name))}");
        }

        if (issues.Count == 0)
            return null;

        var affected = string.Join("; ", issues);
        return BuildIssue(
            $"SSIDs without Roaming Assistant - {affected}. " +
                "Roaming Assistant uses BSS transition frames (soft nudge) instead of hard-disconnecting clients.",
            affected,
            "Per SSID: Settings > WiFi > (SSID) > Advanced > Roaming Assistant. " +
                "Recommended threshold: -70 to -75 dBm.");
    }

    private static HealthIssue? EvaluatePerAp(WiFiOptimizerContext ctx)
    {
        var apsWithout = ctx.AccessPoints
            .Where(ap => ap.Radios.Any(r =>
                r.Band == RadioBand.Band5GHz &&
                r.Channel.HasValue &&
                !r.RoamingAssistantEnabled))
            .ToList();

        if (apsWithout.Count == 0)
            return null;

        var names = string.Join(", ", apsWithout.Select(ap => ap.Name));
        return BuildIssue(
            $"{apsWithout.Count} AP(s) don't have Roaming Assistant enabled on 5 GHz. " +
                "Unlike Minimum RSSI, this uses BSS transition frames (soft nudge) instead of hard-disconnecting clients.",
            names,
            "Per AP: Devices > (AP) > Settings > Radios > 5 GHz > Roaming Assistant. " +
                "Or globally: Settings > WiFi > 5 GHz Roaming Assistant with 'Override All APs'. " +
                "Recommended threshold: -70 to -75 dBm.");
    }

    private static HealthIssue BuildIssue(string description, string affected, string recommendation) => new()
    {
        Severity = HealthIssueSeverity.Info,
        Dimensions = { HealthDimension.RoamingPerformance },
        Title = "Enable Roaming Assistant (Recommended)",
        Description = description,
        AffectedEntity = affected,
        Recommendation = recommendation,
        ScoreImpact = -3,
        ShowOnOverview = false
    };
}
