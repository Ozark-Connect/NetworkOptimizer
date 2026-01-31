using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Utility for evaluating firewall rules considering rule ordering.
/// Firewall rules are processed in index order (lower index = higher priority).
/// A rule with a lower index takes precedence over rules with higher indices.
/// </summary>
public static class FirewallRuleEvaluator
{
    /// <summary>
    /// Result of evaluating firewall rules for a traffic pattern.
    /// </summary>
    public class EvaluationResult
    {
        /// <summary>
        /// The first rule that would match the traffic (considering index order).
        /// Null if no rules match.
        /// </summary>
        public FirewallRule? EffectiveRule { get; init; }

        /// <summary>
        /// Whether traffic is effectively blocked (first matching rule is a block rule that blocks NEW connections).
        /// </summary>
        public bool IsBlocked => EffectiveRule?.ActionType.IsBlockAction() == true
                                 && EffectiveRule.BlocksNewConnections();

        /// <summary>
        /// Whether traffic is effectively allowed (first matching rule is an allow rule,
        /// or no rules match which defaults to the system's default policy).
        /// </summary>
        public bool IsAllowed => EffectiveRule?.ActionType.IsAllowAction() == true;

        /// <summary>
        /// Whether a block rule exists but is eclipsed by an allow rule with lower index.
        /// </summary>
        public bool BlockRuleEclipsed { get; init; }

        /// <summary>
        /// The eclipsed block rule (if any).
        /// </summary>
        public FirewallRule? EclipsedBlockRule { get; init; }

        /// <summary>
        /// Whether an allow rule exists but is eclipsed by a block rule with lower index.
        /// </summary>
        public bool AllowRuleEclipsed { get; init; }

        /// <summary>
        /// The eclipsed allow rule (if any).
        /// </summary>
        public FirewallRule? EclipsedAllowRule { get; init; }
    }

    /// <summary>
    /// Evaluates firewall rules to determine the effective action for traffic matching the given predicate.
    /// Rules are evaluated in index order - lower index = higher priority.
    /// </summary>
    /// <param name="rules">All firewall rules to evaluate.</param>
    /// <param name="matchesPredicate">Predicate that returns true if a rule matches the traffic pattern.</param>
    /// <param name="forNewConnections">If true, skip allow rules that don't allow NEW connections (e.g., RESPOND_ONLY rules).</param>
    /// <returns>Evaluation result indicating the effective rule and whether traffic is blocked/allowed.</returns>
    public static EvaluationResult Evaluate(
        IEnumerable<FirewallRule> rules,
        Func<FirewallRule, bool> matchesPredicate,
        bool forNewConnections = false)
    {
        // Find all matching rules sorted by index (lower = higher priority)
        var matchingRules = rules
            .Where(r => r.Enabled && matchesPredicate(r))
            .OrderBy(r => r.Index)
            .ToList();

        if (matchingRules.Count == 0)
        {
            return new EvaluationResult { EffectiveRule = null };
        }

        // When evaluating for NEW connections, skip allow rules that don't allow NEW connections
        // (e.g., RESPOND_ONLY rules that only allow ESTABLISHED/RELATED traffic)
        FirewallRule? effectiveRule;
        if (forNewConnections)
        {
            effectiveRule = matchingRules.FirstOrDefault(r =>
                r.ActionType.IsBlockAction() || r.AllowsNewConnections());

            if (effectiveRule == null)
            {
                return new EvaluationResult { EffectiveRule = null };
            }
        }
        else
        {
            effectiveRule = matchingRules[0];
        }

        // Check for eclipsed rules
        FirewallRule? eclipsedBlockRule = null;
        FirewallRule? eclipsedAllowRule = null;

        if (effectiveRule.ActionType == FirewallAction.Accept)
        {
            // Allow rule is effective - check if any block rules are eclipsed
            eclipsedBlockRule = matchingRules
                .Where(r => r.Index > effectiveRule.Index)
                .FirstOrDefault(r => r.ActionType.IsBlockAction() && r.BlocksNewConnections());
        }
        else if (effectiveRule.ActionType.IsBlockAction())
        {
            // Block rule is effective - check if any allow rules are eclipsed
            eclipsedAllowRule = matchingRules
                .Where(r => r.Index > effectiveRule.Index)
                .FirstOrDefault(r => r.ActionType == FirewallAction.Accept && (!forNewConnections || r.AllowsNewConnections()));
        }

        return new EvaluationResult
        {
            EffectiveRule = effectiveRule,
            BlockRuleEclipsed = eclipsedBlockRule != null,
            EclipsedBlockRule = eclipsedBlockRule,
            AllowRuleEclipsed = eclipsedAllowRule != null,
            EclipsedAllowRule = eclipsedAllowRule
        };
    }

    /// <summary>
    /// Checks if traffic is effectively blocked considering rule ordering.
    /// Returns true only if a block rule (that blocks NEW connections) takes effect
    /// before any allow rule that matches the same traffic.
    /// </summary>
    /// <param name="rules">All firewall rules to evaluate.</param>
    /// <param name="matchesPredicate">Predicate that returns true if a rule matches the traffic pattern.</param>
    /// <returns>True if traffic is effectively blocked.</returns>
    public static bool IsTrafficBlocked(
        IEnumerable<FirewallRule> rules,
        Func<FirewallRule, bool> matchesPredicate)
    {
        return Evaluate(rules, matchesPredicate).IsBlocked;
    }

    /// <summary>
    /// Checks if traffic is effectively allowed considering rule ordering.
    /// Returns true if an allow rule takes effect before any block rule that matches the same traffic.
    /// </summary>
    /// <param name="rules">All firewall rules to evaluate.</param>
    /// <param name="matchesPredicate">Predicate that returns true if a rule matches the traffic pattern.</param>
    /// <returns>True if traffic is effectively allowed.</returns>
    public static bool IsTrafficAllowed(
        IEnumerable<FirewallRule> rules,
        Func<FirewallRule, bool> matchesPredicate)
    {
        return Evaluate(rules, matchesPredicate).IsAllowed;
    }

    /// <summary>
    /// Gets the first effective block rule that would apply to traffic, considering rule ordering.
    /// Returns null if no block rule takes effect (either none exist or an allow rule eclipses them).
    /// </summary>
    /// <param name="rules">All firewall rules to evaluate.</param>
    /// <param name="matchesPredicate">Predicate that returns true if a rule matches the traffic pattern.</param>
    /// <returns>The effective block rule, or null if traffic is not blocked.</returns>
    public static FirewallRule? GetEffectiveBlockRule(
        IEnumerable<FirewallRule> rules,
        Func<FirewallRule, bool> matchesPredicate)
    {
        var result = Evaluate(rules, matchesPredicate);
        return result.IsBlocked ? result.EffectiveRule : null;
    }

    /// <summary>
    /// Gets the first effective allow rule that would apply to traffic, considering rule ordering.
    /// Returns null if no allow rule takes effect (either none exist or a block rule eclipses them).
    /// </summary>
    /// <param name="rules">All firewall rules to evaluate.</param>
    /// <param name="matchesPredicate">Predicate that returns true if a rule matches the traffic pattern.</param>
    /// <returns>The effective allow rule, or null if traffic is not allowed.</returns>
    public static FirewallRule? GetEffectiveAllowRule(
        IEnumerable<FirewallRule> rules,
        Func<FirewallRule, bool> matchesPredicate)
    {
        var result = Evaluate(rules, matchesPredicate);
        return result.IsAllowed ? result.EffectiveRule : null;
    }
}
