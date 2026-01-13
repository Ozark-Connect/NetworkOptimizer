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
    private static int _unusedPortInactivityDays = 15;
    private static int _namedPortInactivityDays = 45;

    public static void SetLogger(ILogger logger) => _logger = logger;

    /// <summary>
    /// Configure the inactivity thresholds for unused port detection.
    /// </summary>
    /// <param name="unusedPortDays">Days before flagging an unnamed port (default 15)</param>
    /// <param name="namedPortDays">Days before flagging a named port (default 45)</param>
    public static void SetThresholds(int unusedPortDays, int namedPortDays)
    {
        _unusedPortInactivityDays = unusedPortDays;
        _namedPortInactivityDays = namedPortDays;
    }

    public override string RuleId => "UNUSED-PORT-001";
    public override string RuleName => "Unused Port Disabled";
    public override string Description => "Unused ports should be disabled (forward: disabled) to prevent unauthorized access";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 2;

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
        var hasCustomName = PortNameHelper.IsCustomPortName(port.Name);
        var thresholdDays = hasCustomName ? _namedPortInactivityDays : _unusedPortInactivityDays;

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
                { "recommendation", "Set forward mode to 'disabled' to harden the switch" },
                { "configurable_setting", "Configure the grace period before flagging disconnected ports in Settings." }
            });
    }
}
