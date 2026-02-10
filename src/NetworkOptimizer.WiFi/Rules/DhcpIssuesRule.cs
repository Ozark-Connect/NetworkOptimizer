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
                "This typically indicates connectivity issues such as weak signal or interference.",
            AffectedEntity = clientsWithoutIp.Count <= 5
                ? string.Join(", ", clientsWithoutIp.Select(c => c.Name))
                : $"{string.Join(", ", clientsWithoutIp.Take(5).Select(c => c.Name))} +{clientsWithoutIp.Count - 5} more",
            AffectedClientMac = clientsWithoutIp.Count == 1 ? clientsWithoutIp[0].Mac : null,
            Recommendation = "Check for connectivity issues first - is the client in a weak signal area or experiencing interference? " +
                "If connectivity is good, verify your DHCP pool isn't exhausted in UniFi: Settings > Networks > (Network) > DHCP Range.",
            ScoreImpact = -10,
            ShowOnOverview = false
        };
    }
}
