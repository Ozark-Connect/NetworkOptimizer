using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects unused ports that are not disabled.
/// Unused ports should be disabled to prevent unauthorized connections.
/// Uses different inactivity thresholds based on whether the port has a custom name.
/// </summary>
public class UnusedPortRule : AuditRuleBase
{
    private static ILogger? _logger;

    public static void SetLogger(ILogger logger) => _logger = logger;

    public override string RuleId => "UNUSED-PORT-001";
    public override string RuleName => "Unused Port Disabled";
    public override string Description => "Unused ports should be disabled (forward: disabled) to prevent unauthorized access";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 2;

    // Number of days a port must be inactive before flagging
    private const int DefaultInactivityThresholdDays = 15;
    private const int NamedPortInactivityThresholdDays = 45;

    // Default port name patterns - ports with these names are considered unnamed
    private static readonly Regex DefaultPortNamePattern = new(
        @"^(Port\s*\d+|SFP\+?\s*\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks)
    {
        // Only check ports that are down
        if (port.IsUp)
            return null;

        // Skip uplinks and WAN ports
        if (port.IsUplink || port.IsWan)
            return null;

        // Check if port is disabled
        if (port.ForwardMode == "disabled")
            return null; // Correctly configured

        // Determine threshold based on whether port has a custom name
        var hasCustomName = !string.IsNullOrEmpty(port.Name) && !IsDefaultPortName(port.Name);
        var thresholdDays = hasCustomName ? NamedPortInactivityThresholdDays : DefaultInactivityThresholdDays;

        // Check if a device was connected recently (within threshold)
        if (port.LastConnectionSeen.HasValue)
        {
            var lastSeen = DateTimeOffset.FromUnixTimeSeconds(port.LastConnectionSeen.Value);
            var daysSinceLastConnection = (DateTimeOffset.UtcNow - lastSeen).TotalDays;

            if (daysSinceLastConnection < thresholdDays)
            {
                // Device was connected recently - don't flag
                return null;
            }
        }

        // Debug logging for flagged ports
        _logger?.LogInformation("UnusedPortRule flagging {Switch} port {Port}: forward='{Forward}', isUp={IsUp}, lastSeen={LastSeen}, threshold={Threshold}d",
            port.Switch.Name, port.PortIndex, port.ForwardMode, port.IsUp, port.LastConnectionSeen, thresholdDays);

        return CreateIssue(
            "Unused port not disabled - should set forward mode to 'disabled'",
            port,
            new Dictionary<string, object>
            {
                { "current_forward_mode", port.ForwardMode ?? "unknown" },
                { "recommendation", "Set forward mode to 'disabled' to harden the switch" }
            });
    }

    private static bool IsDefaultPortName(string name)
    {
        return string.IsNullOrWhiteSpace(name) || DefaultPortNamePattern.IsMatch(name.Trim());
    }
}
