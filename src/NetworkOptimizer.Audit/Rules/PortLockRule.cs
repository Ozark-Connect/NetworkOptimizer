using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects ports with a UniFi device connected that are not locked to it.
/// Lock Port to UniFi Device (UniFi Network 10.6.101+, switch firmware 7.6.2+) ties the port
/// to one UniFi device - Network, Protect, or any other UniFi app - so it is the fit for
/// infrastructure ports where MAC restriction is the wrong tool. Trunk ports count: an AP
/// downlink is the lock's main use. Silent when the versions are not met or an Ethernet Port
/// Profile blocks the lock; MacRestrictionRule covers those ports. A port already MAC-restricted
/// to a UniFi device gets an Informational issue (no score impact) offering the lock instead.
/// </summary>
public class PortLockRule : AuditRuleBase
{
    public override string RuleId => IssueTypes.PortLock;
    public override string RuleName => "Lock Port to UniFi Device";
    public override string Description => "Ports with a UniFi device connected should be locked to that device";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 3;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        // A lock needs a live device to lock to
        if (!port.IsUp)
            return null;

        if (port.ForwardMode == "disabled" || port.IsUplink || port.IsWan || port.IsMirrorDestination)
            return null;

        if (!port.HasUniFiDevice)
            return null;

        if (port.IsPortLocked)
            return null;

        // 802.1X is its own model; the lock is not offered as a replacement for RADIUS
        if (port.IsDot1xSecured)
            return null;

        // A profile that deliberately leaves the port open to any device is not a port to lock
        if (HasIntentionalUnrestrictedProfile(port))
            return null;

        // Versions met and no Ethernet Port Profile in the way (PortLockSupport owns that rule)
        if (!IsPortLockAvailable(port))
            return null;

        var device = DescribeUniFiDevice(port);
        var network = GetNetwork(port.NativeNetworkId, networks);
        var metadata = new Dictionary<string, object>
        {
            { "network", network?.Name ?? "Unknown" },
            { "device", device },
            { "device_type", port.ConnectedDeviceType ?? port.ConnectedClient?.UniFiProductLine ?? "unknown" }
        };

        // Already MAC-restricted: secured, so no score impact. The lock is offered as the simpler tool.
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false))
        {
            return CreateIssue(
                $"Port is MAC-restricted for {device}; Lock Port to UniFi Device can replace the MAC list",
                port,
                metadata,
                $"This port already restricts by MAC address. Lock Port to UniFi Device ties the port to {device} " +
                "without a MAC list to maintain, so a replaced or re-addressed device does not need the list edited. " +
                "In UniFi Network, open this port in Port Manager, enable Lock Port to UniFi Device, and clear the MAC restriction.",
                overrideSeverity: AuditSeverity.Informational,
                overrideScoreImpact: 0);
        }

        return CreateIssue(
            $"Port should be locked to {device} with Lock Port to UniFi Device",
            port,
            metadata,
            $"In UniFi Network, open this port in Port Manager and enable Lock Port to UniFi Device. " +
            $"Only {device} can then use the port; anything else plugged in is blocked without maintaining a MAC list.");
    }
}
