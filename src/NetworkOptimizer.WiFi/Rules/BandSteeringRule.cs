namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that recommends enabling band steering when a significant percentage
/// of clients are on a lower band than they support.
/// </summary>
public class BandSteeringRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-BAND-STEERING-001";

    /// <summary>
    /// Minimum percentage of steerable clients to trigger this recommendation.
    /// </summary>
    private const double MinSteerablePctThreshold = 30;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Check percentage of clients that could be on higher band
        var steerablePct = ctx.Clients.Count > 0
            ? (double)ctx.SteerableClients.Count / ctx.Clients.Count * 100
            : 0;

        if (steerablePct < MinSteerablePctThreshold)
            return null; // Not enough to warrant recommendation

        // Check if any main SSID lacks band steering
        var mainSsidsWithoutSteering = ctx.Wlans
            .Where(w => w.Enabled && !w.IsGuest && !w.BandSteeringEnabled)
            .ToList();

        if (mainSsidsWithoutSteering.Count == 0)
            return null; // All have band steering

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.BandSteering, HealthDimension.AirtimeEfficiency },
            Title = "Enable or Strengthen Band Steering",
            Description = $"{steerablePct:F0}% of clients ({ctx.SteerableClients.Count}) are on a lower band than they support. " +
                "Enable band steering to push capable devices to faster bands.",
            AffectedEntity = string.Join(", ", mainSsidsWithoutSteering.Select(w => w.Name)),
            Recommendation = "In UniFi Network: Settings > WiFi > (SSID) > Advanced > Band Steering - " +
                "enable 'Prefer 5GHz' or 'Prefer 6GHz'.",
            ScoreImpact = -10
        };
    }
}
