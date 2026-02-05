using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that recommends creating a separate IoT SSID for legacy 2.4 GHz devices.
/// This allows enabling aggressive band steering on the main SSID without breaking
/// legacy devices that can only connect to 2.4 GHz.
///
/// Condition is satisfied (no issue) when:
/// - There are fewer than 5 legacy clients, OR
/// - IoT network exists AND has an SSID bound to it AND main SSIDs have band steering
/// </summary>
public class IoTSsidSeparationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-IOT-SSID-001";

    /// <summary>
    /// Minimum number of legacy clients to trigger this recommendation.
    /// </summary>
    private const int MinLegacyClientsThreshold = 5;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only applies if there are enough legacy devices
        if (ctx.LegacyClients.Count < MinLegacyClientsThreshold)
            return null;

        // Check if user already has IoT SSID + band steering setup
        var iotNetwork = ctx.IoTNetwork;
        if (iotNetwork != null)
        {
            // Check if IoT network has an SSID bound to it
            var iotSsid = ctx.Wlans.FirstOrDefault(w => w.Enabled && w.NetworkId == iotNetwork.Id);
            if (iotSsid != null)
            {
                // IoT SSID exists - check if main SSIDs have band steering
                var mainSsids = ctx.Wlans
                    .Where(w => w.Enabled && !w.IsGuest && w.Id != iotSsid.Id)
                    .ToList();

                // If there are no main SSIDs (unusual), don't recommend
                if (mainSsids.Count == 0)
                    return null;

                // Check if all main SSIDs have band steering enabled
                if (mainSsids.All(w => w.BandSteeringEnabled))
                    return null; // Already properly configured!
            }
        }

        // Issue: No IoT SSID or main SSIDs lack band steering
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.ChannelHealth },
            Title = "Legacy Device Airtime Impact",
            Description = $"You have {ctx.LegacyClients.Count} legacy 2.4 GHz-only devices. " +
                "A separate IoT SSID lets you enable aggressive band steering on your main SSID " +
                "without breaking these devices.",
            Recommendation = "Create a 2.4 GHz-only SSID for IoT/legacy devices (with band steering off), " +
                "then enable band steering on your main SSID to push capable devices to 5 GHz.",
            ScoreImpact = -5
        };
    }
}
