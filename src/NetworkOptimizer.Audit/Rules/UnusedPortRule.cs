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

    /// <summary>
    /// Maximum reasonable age for a lastSeen timestamp. Timestamps older than this
    /// are considered invalid data from the UniFi API (e.g., corrupted values after
    /// power events) and are ignored rather than flagging the port.
    /// </summary>
    private const int MaxReasonableAgeDays = 3650; // 10 years

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
    public override bool AppliesToLagChildPorts => true;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
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

        // Skip if port has an intentional unrestricted access profile
        // (user has created an access port profile with MAC restriction disabled - like hotel RJ45 jacks)
        if (HasIntentionalUnrestrictedProfile(port))
            return null;

        // Determine threshold based on whether port has a custom name
        var hasCustomName = PortNameHelper.IsCustomPortName(port.Name);
        var thresholdDays = hasCustomName ? _namedPortInactivityDays : _unusedPortInactivityDays;

        // Check if a device was connected recently (within threshold)
        if (port.LastConnectionSeen.HasValue)
        {
            var lastSeen = DateTimeOffset.FromUnixTimeSeconds(port.LastConnectionSeen.Value);
            var daysSinceLastConnection = (DateTimeOffset.UtcNow - lastSeen).TotalDays;

            // Treat absurdly old timestamps as invalid data (likely UniFi API bug after power events)
            // Don't flag these ports - we can't trust the data
            if (daysSinceLastConnection > MaxReasonableAgeDays)
            {
                _logger?.LogWarning(
                    "UnusedPortRule ignoring invalid lastSeen for {Switch} port {Port}: timestamp={Timestamp} ({Days:F0} days ago exceeds {Max} day maximum)",
                    port.Switch.Name, port.PortIndex, port.LastConnectionSeen, daysSinceLastConnection, MaxReasonableAgeDays);
                return null;
            }

            if (daysSinceLastConnection < thresholdDays)
            {
                // Device was connected recently - don't flag
                return null;
            }
        }

        // Debug logging for flagged ports
        _logger?.LogInformation("UnusedPortRule flagging {Switch} port {Port}: forward='{Forward}', isUp={IsUp}, lastSeenDaysAgo={LastSeenDays}, threshold={Threshold}d",
            port.Switch.Name, port.PortIndex, port.ForwardMode, port.IsUp,
            port.LastConnectionSeen.HasValue
                ? $"{(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(port.LastConnectionSeen.Value)).TotalDays:F0}"
                : "none",
            thresholdDays);

        return CreateIssue(
            "Unused port should be set to Disabled or disabled via an Ethernet Port Profile in UniFi Network",
            port,
            new Dictionary<string, object>
            {
                { "current_forward_mode", port.ForwardMode ?? "unknown" },
                { "configurable_setting", "Configure the grace period before flagging disconnected ports in Settings." }
            },
            "Disable unused ports to reduce attack surface. " +
            "In UniFi, set the port to 'Disabled' to prevent unauthorized device connections.");
    }
}
