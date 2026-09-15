using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects ports with a UniFi device connected that are not locked to it.
/// Lock Port to UniFi Device (UniFi Network 10.6.101+, switch firmware 7.6.2+) ties the port
/// to one UniFi device - Network, Protect, or any other UniFi app - so it is the fit for
/// infrastructure ports where MAC restriction is the wrong tool. Trunk ports count: an AP
/// downlink is the lock's main use. Silent when the versions are not met or an Ethernet Port
/// Profile blocks the lock; MacRestrictionRule covers those ports.
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

        // Already held by another port security mechanism; the lock is an alternative, not a second layer
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false) || port.IsDot1xSecured)
            return null;

        // A profile that deliberately leaves the port open to any device is not a port to lock
        if (HasIntentionalUnrestrictedProfile(port))
            return null;

        // Versions met and no Ethernet Port Profile in the way (PortLockSupport owns that rule)
        if (!IsPortLockAvailable(port))
            return null;

        var device = DescribeUniFiDevice(port);
        var network = GetNetwork(port.NativeNetworkId, networks);

        return CreateIssue(
            $"Port should be locked to {device} with Lock Port to UniFi Device",
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" },
                { "device", device },
                { "device_type", port.ConnectedDeviceType ?? port.ConnectedClient?.UniFiProductLine ?? "unknown" }
            },
            $"In UniFi Network, open this port in Port Manager and enable Lock Port to UniFi Device. " +
            $"Only {device} can then use the port; anything else plugged in is blocked without maintaining a MAC list.");
    }
}
