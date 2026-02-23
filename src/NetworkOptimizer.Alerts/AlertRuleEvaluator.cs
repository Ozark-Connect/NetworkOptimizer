using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Evaluates alert events against configured rules to determine which rules match.
/// </summary>
public class AlertRuleEvaluator
{
    private readonly AlertCooldownTracker _cooldownTracker;

    public AlertRuleEvaluator(AlertCooldownTracker cooldownTracker)
    {
        _cooldownTracker = cooldownTracker;
    }

    /// <summary>
    /// Find all rules that match the given event and are not in cooldown.
    /// </summary>
    public List<AlertRule> Evaluate(AlertEvent alertEvent, IReadOnlyList<AlertRule> rules)
    {
        var matches = new List<AlertRule>();

        foreach (var rule in rules)
        {
            if (!rule.IsEnabled)
                continue;

            if (alertEvent.Severity < rule.MinSeverity)
                continue;

            if (!MatchesEventType(alertEvent.EventType, rule.EventTypePattern))
                continue;

            if (!string.IsNullOrEmpty(rule.Source) &&
                !string.Equals(rule.Source, alertEvent.Source, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!MatchesTargetDevice(alertEvent.DeviceId, alertEvent.DeviceIp, rule.TargetDevices))
                continue;

            if (!MeetsThreshold(alertEvent, rule))
                continue;

            var cooldownKey = $"{rule.Id}:{alertEvent.DeviceId ?? alertEvent.DeviceIp ?? "global"}";
            if (_cooldownTracker.IsInCooldown(cooldownKey, rule.CooldownSeconds))
                continue;

            matches.Add(rule);
        }

        return matches;
    }

    /// <summary>
    /// Record that a rule was fired (for cooldown tracking).
    /// </summary>
    public void RecordFired(AlertRule rule, AlertEvent alertEvent)
    {
        var cooldownKey = $"{rule.Id}:{alertEvent.DeviceId ?? alertEvent.DeviceIp ?? "global"}";
        _cooldownTracker.RecordFired(cooldownKey);
    }

    /// <summary>
    /// Match event type against pattern. Supports trailing wildcard (e.g., "audit.*" matches "audit.score_dropped").
    /// </summary>
    internal static bool MatchesEventType(string eventType, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return true;

        if (pattern.EndsWith(".*"))
        {
            var prefix = pattern[..^2];
            return eventType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                   eventType.Length > prefix.Length && eventType[prefix.Length] == '.';
        }

        return string.Equals(eventType, pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if the event meets the rule's degradation threshold.
    /// If the rule has a ThresholdPercent, the event must have a "drop_percent" context value >= threshold.
    /// </summary>
    private static bool MeetsThreshold(AlertEvent alertEvent, AlertRule rule)
    {
        if (rule.ThresholdPercent == null)
            return true;

        if (alertEvent.Context.TryGetValue("drop_percent", out var dropStr) ||
            alertEvent.Context.TryGetValue("drop", out dropStr))
        {
            if (double.TryParse(dropStr, out var dropValue))
                return dropValue >= rule.ThresholdPercent.Value;
        }

        // No drop context = not a threshold event, let it through
        return true;
    }

    /// <summary>
    /// Check if event matches the rule's target device filter.
    /// </summary>
    private static bool MatchesTargetDevice(string? deviceId, string? deviceIp, string? targetDevices)
    {
        if (string.IsNullOrEmpty(targetDevices))
            return true;

        var targets = targetDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.Length == 0)
            return true;

        if (!string.IsNullOrEmpty(deviceId) && targets.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(deviceIp) && targets.Contains(deviceIp, StringComparer.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
