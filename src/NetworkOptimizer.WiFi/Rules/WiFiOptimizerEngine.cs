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
        foreach (var rule in _rules)
        {
            try
            {
                var ruleIssues = rule.EvaluateAll(context).ToList();
                foreach (var issue in ruleIssues)
                {
                    score.Issues.Add(issue);
                    _logger.LogDebug("Rule {RuleId} produced issue: {Title}", rule.RuleId, issue.Title);
                }

                if (ruleIssues.Count == 0)
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
                var ruleIssues = rule.EvaluateAll(context).ToList();
                foreach (var issue in ruleIssues)
                {
                    issues.Add(issue);
                    _logger.LogDebug("Rule {RuleId} produced issue: {Title}", rule.RuleId, issue.Title);
                }

                if (ruleIssues.Count == 0)
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
