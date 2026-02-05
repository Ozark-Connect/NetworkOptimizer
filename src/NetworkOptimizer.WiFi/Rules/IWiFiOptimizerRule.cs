namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Interface for WiFi Optimizer rules that detect configuration issues
/// and generate recommendations.
/// </summary>
public interface IWiFiOptimizerRule
{
    /// <summary>
    /// Unique rule identifier for tracking/suppression.
    /// </summary>
    string RuleId { get; }

    /// <summary>
    /// Evaluate the rule against current context.
    /// Returns null if the condition is already satisfied (no issue).
    /// Returns a HealthIssue if there's an actionable recommendation.
    /// </summary>
    HealthIssue? Evaluate(WiFiOptimizerContext context);

    /// <summary>
    /// Evaluate the rule and return multiple issues (for rules that can generate multiple issues).
    /// Default implementation calls Evaluate and returns single-item or empty enumerable.
    /// </summary>
    IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext context)
    {
        var issue = Evaluate(context);
        if (issue != null)
            yield return issue;
    }
}
