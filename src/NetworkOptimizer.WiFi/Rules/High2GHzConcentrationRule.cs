using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that flags when too many clients are on 2.4 GHz even though some
/// support higher bands. This indicates band steering isn't effective.
/// </summary>
public class High2GHzConcentrationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-2GHZ-CONCENTRATION-001";

    /// <summary>
    /// Minimum percentage of clients on 2.4 GHz to trigger this recommendation.
    /// </summary>
    private const double Min2GHzPctThreshold = 50;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        if (ctx.Clients.Count == 0)
            return null;

        var clientsOn2g = ctx.Clients.Count(c => c.Band == RadioBand.Band2_4GHz);
        var pct2g = (double)clientsOn2g / ctx.Clients.Count * 100;

        // Only flag if >50% on 2.4GHz AND some of those could use a higher band. A 5 GHz client that
        // could use 6 GHz is steerable too, but it is not part of the 2.4 GHz crowd.
        var steerable2g = ctx.SteerableClients.Where(c => c.Band == RadioBand.Band2_4GHz).ToList();
        if (pct2g <= Min2GHzPctThreshold || steerable2g.Count == 0)
            return null;

        var link = steerable2g.Count == 1
            ? new IssueLink(string.IsNullOrEmpty(steerable2g[0].Name) ? steerable2g[0].Mac : steerable2g[0].Name, IssueLinks.Client(steerable2g[0].Mac))
            : new IssueLink("2.4 GHz clients", IssueLinks.Clients(band: RadioBand.Band2_4GHz));

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.BandSteering },
            Title = "High 2.4 GHz Concentration",
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId),
            Description = $"{pct2g:F0}% of clients are on 2.4 GHz, but {steerable2g.Count} of them " +
                "support higher bands. This leads to congestion and slower speeds on 2.4 GHz.",
            Recommendation = Recommendation(ctx, steerable2g),
            ScoreImpact = -10
        }.WithLinks(new[] { link });
    }

    /// <summary>Only the options the site has not already taken: band steering on their SSIDs, an IoT SSID.</summary>
    private static string Recommendation(WiFiOptimizerContext ctx, List<WirelessClientSnapshot> steerable2g)
    {
        var iotSsidIds = ctx.IoTSsids.Select(w => w.Id).ToHashSet();
        var clientSsids = steerable2g.Select(c => c.Essid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var theirWlans = ctx.Wlans.Where(w => w.Enabled && clientSsids.Contains(w.Name)).ToList();
        // A client whose SSID is unknown is judged by the main SSIDs instead.
        if (theirWlans.Count == 0)
            theirWlans = ctx.Wlans.Where(w => w.Enabled && !w.IsGuest && !iotSsidIds.Contains(w.Id)).ToList();
        var steeringOn = theirWlans.Count > 0 && theirWlans.All(w => w.BandSteeringEnabled);

        var options = new List<string>();
        if (!steeringOn) options.Add("enabling band steering");
        options.Add("reducing 2.4 GHz TX power to encourage clients to use 5 GHz");
        if (iotSsidIds.Count == 0) options.Add("creating a separate IoT SSID for 2.4 GHz-only devices");

        var prefix = steeringOn
            ? $"Band steering is already on for {string.Join(", ", theirWlans.Select(w => w.Name))}, so " +
              (steerable2g.Count == 1 ? "this client stays on 2.4 GHz on its own. " : "these clients stay on 2.4 GHz on their own. ")
            : "";
        if (options.Count == 1)
            return $"{prefix}Consider {options[0]}.";
        var numbered = options.Select((o, i) => $"({i + 1}) {o}").ToList();
        return $"{prefix}Consider: {string.Join(", ", numbered.Take(numbered.Count - 1))}, or {numbered[^1]}.";
    }
}
