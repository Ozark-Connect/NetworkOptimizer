namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects clients without IP addresses, indicating DHCP issues
/// such as pool exhaustion or server unreachability.
/// </summary>
public class DhcpIssuesRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-DHCP-ISSUES-001";

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        if (ctx.Clients.Count == 0)
            return null;

        // Count clients without IP addresses (connected but no DHCP lease)
        var clientsWithoutIp = ctx.Clients
            .Where(c => c.IsAuthorized && string.IsNullOrEmpty(c.Ip))
            .ToList();

        if (clientsWithoutIp.Count == 0)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.ClientSatisfaction },
            Title = "DHCP Issues Detected",
            Description = $"{clientsWithoutIp.Count} client(s) connected but failed to get an IP address. " +
                "This often indicates DHCP pool exhaustion or server issues.",
            AffectedEntity = clientsWithoutIp.Count <= 5
                ? string.Join(", ", clientsWithoutIp.Select(c => c.Name))
                : $"{string.Join(", ", clientsWithoutIp.Take(5).Select(c => c.Name))} +{clientsWithoutIp.Count - 5} more",
            Recommendation = "Check your DHCP server settings. Ensure the IP pool is large enough and the lease time isn't too long. " +
                "In UniFi: Settings > Networks > (Network) > DHCP Range.",
            ScoreImpact = -10,
            ShowOnOverview = false
        };
    }
}
