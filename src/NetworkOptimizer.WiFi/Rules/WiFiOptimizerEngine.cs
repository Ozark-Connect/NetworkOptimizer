using Microsoft.Extensions.Logging;
using NetworkOptimizer.WiFi.Helpers;

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
        foreach (var issue in EvaluateRules(context))
            score.Issues.Add(issue);
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
                    Stamp(rule, issue);
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

    /// <summary>
    /// Fills what every issue must carry. A rule that set no key gets one from its subject, and
    /// one that set no class is read as Measured, which is the reading intent can never soften.
    /// </summary>
    private void Stamp(IWiFiOptimizerRule rule, HealthIssue issue)
    {
        issue.RuleId = rule.RuleId;
        if (string.IsNullOrEmpty(issue.Key))
        {
            issue.Key = HealthIssueKeys.For(rule.RuleId,
                HealthIssueKeys.Names(new[] { issue.AffectedEntity ?? issue.Title }));
            _logger.LogDebug("Rule {RuleId} set no issue key; using {Key}", rule.RuleId, issue.Key);
        }
        if (issue.Class == HealthIssueClass.Unclassified)
        {
            issue.Class = HealthIssueClass.Measured;
            _logger.LogDebug("Rule {RuleId} set no issue class; reading it as Measured", rule.RuleId);
        }
    }
}
