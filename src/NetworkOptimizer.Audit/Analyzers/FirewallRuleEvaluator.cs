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
    /// <param name="family">When set, only rules that match this address family take part.</param>
    /// <returns>Evaluation result indicating the effective rule and whether traffic is blocked/allowed.</returns>
    public static EvaluationResult Evaluate(
        IEnumerable<FirewallRule> rules,
        Func<FirewallRule, bool> matchesPredicate,
        bool forNewConnections = false,
        IpFamily? family = null)
    {
        // Find all matching rules sorted by index (lower = higher priority)
        var matchingRules = rules
            .Where(r => r.Enabled && (family is not { } f || r.MatchesIpFamily(f)) && matchesPredicate(r))
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
                (r.ActionType.IsBlockAction() && r.BlocksNewConnections()) ||
                (r.ActionType.IsAllowAction() && r.AllowsNewConnections()));

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
    /// The rule that decides one new connection, for a match that can answer "cannot tell". Rules are walked
    /// in index order as UniFi does; the first that matches decides. When a rule above it cannot be judged
    /// (a client source with no MAC, a web or app destination), the answer stops there as
    /// <see cref="FlowDecision.Undecided"/>: naming a later rule would be a guess that rule may override.
    /// </summary>
    /// <param name="rules">The zone pair's rules.</param>
    /// <param name="matches">True, false, or null (cannot tell) for whether a rule matches the connection.</param>
    /// <param name="family">The connection's address family.</param>
    public static FlowDecision DecideFlow(IEnumerable<FirewallRule> rules, Func<FirewallRule, bool?> matches, IpFamily family)
    {
        foreach (var rule in rules.Where(r => r.Enabled && r.MatchesIpFamily(family)).OrderBy(r => r.Index))
        {
            // Only a rule that acts on new connections can decide one (not a RESPOND_ONLY allow).
            var acts = (rule.ActionType.IsBlockAction() && rule.BlocksNewConnections())
                || (rule.ActionType.IsAllowAction() && rule.AllowsNewConnections());
            if (!acts) continue;
            switch (matches(rule))
            {
                case false: continue;
                case null: return new FlowDecision(null, rule);
                default: return new FlowDecision(rule, null);
            }
        }
        return new FlowDecision(null, null);
    }

    /// <summary>The outcome of <see cref="DecideFlow"/>.</summary>
    /// <param name="Rule">The deciding rule, or null.</param>
    /// <param name="Undecided">The first rule that could not be judged, ahead of any match; null when none.</param>
    public sealed record FlowDecision(FirewallRule? Rule, FirewallRule? Undecided);

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
