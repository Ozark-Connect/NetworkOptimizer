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
}
