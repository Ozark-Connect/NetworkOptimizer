using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Engine that evaluates all registered WiFi Optimizer rules and collects issues.
/// </summary>
public class WiFiOptimizerEngine
{
    private readonly IEnumerable<IWiFiOptimizerRule> _rules;
    private readonly ILogger<WiFiOptimizerEngine> _logger;

    public WiFiOptimizerEngine(
        IEnumerable<IWiFiOptimizerRule> rules,
        ILogger<WiFiOptimizerEngine> logger)
    {
        _rules = rules;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate all rules against the given context and add issues to the health score.
    /// </summary>
    public void EvaluateRules(SiteHealthScore score, WiFiOptimizerContext context)
    {
        // Debug: Log context state for IoT detection debugging
        _logger.LogDebug("WiFiOptimizer context: {NetworkCount} networks, {WlanCount} WLANs, {LegacyCount} legacy clients",
            context.Networks.Count, context.Wlans.Count, context.LegacyClients.Count);

        foreach (var network in context.Networks)
        {
            _logger.LogDebug("Network: {Name} (Id={Id}, Purpose={Purpose}, Enabled={Enabled})",
                network.Name, network.Id, network.Purpose, network.Enabled);
        }

        foreach (var wlan in context.Wlans)
        {
            _logger.LogDebug("WLAN: {Name} (Id={Id}, NetworkId={NetworkId}, Enabled={Enabled}, BandSteering={BandSteering})",
                wlan.Name, wlan.Id, wlan.NetworkId ?? "null", wlan.Enabled, wlan.BandSteeringEnabled);
        }

        var iotNetworks = context.IoTNetworks.ToList();
        _logger.LogDebug("IoT networks found: {Count} ({Names})",
            iotNetworks.Count, string.Join(", ", iotNetworks.Select(n => n.Name)));

        foreach (var rule in _rules)
        {
            try
            {
                var issue = rule.Evaluate(context);
                if (issue != null)
                {
                    score.Issues.Add(issue);
                    _logger.LogDebug("Rule {RuleId} produced issue: {Title}", rule.RuleId, issue.Title);
                }
                else
                {
                    _logger.LogDebug("Rule {RuleId} satisfied (no issue)", rule.RuleId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rule {RuleId} failed", rule.RuleId);
            }
        }
    }

    /// <summary>
    /// Evaluate all rules and return the issues (without adding to a score).
    /// </summary>
    public List<HealthIssue> EvaluateRules(WiFiOptimizerContext context)
    {
        var issues = new List<HealthIssue>();

        foreach (var rule in _rules)
        {
            try
            {
                var issue = rule.Evaluate(context);
                if (issue != null)
                {
                    issues.Add(issue);
                    _logger.LogDebug("Rule {RuleId} produced issue: {Title}", rule.RuleId, issue.Title);
                }
                else
                {
                    _logger.LogDebug("Rule {RuleId} satisfied (no issue)", rule.RuleId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rule {RuleId} failed", rule.RuleId);
            }
        }

        return issues;
    }
}
