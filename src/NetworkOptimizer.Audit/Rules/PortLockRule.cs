using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects ports with a UniFi device connected that are not locked to it.
/// Lock Port to UniFi Device (UniFi switches only, UniFi Network 10.6.101+, switch firmware 7.6.2+) ties the port
/// to one UniFi device - Network, Protect, or any other UniFi app - so it is the fit for
/// infrastructure ports where MAC restriction is the wrong tool. Trunk ports count: an AP
/// downlink is the lock's main use. Silent when the versions are not met or the port is a LAG;
/// MacRestrictionRule covers those ports. Also silent on a port several clients used in the
/// shared-port window, where MacRestrictionRule carries the issue and the score.
/// Informational (no score impact): a port already MAC-restricted to a single UniFi device, offering
/// the lock as the alternative, and a profiled port MacRestrictionRule does not cover (a fabric device
/// or a trunk), offering the lock if the profile is removed.
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

        // UniFi offers the lock on downlinks only; an uplink keeps its Port Profile
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

        // A UniFi switch port, versions met, not a LAG. A profile is handled below: it blocks the lock but not the suggestion.
        if (!CanPortEverLock(port) || !IsPortLockSupported(port))
            return null;

        // Several clients used the port recently: a lock would block the others. MacRestrictionRule owns it.
        if (port.IsSharedPort)
            return null;

        var blockedByProfile = IsPortLockBlockedByProfile(port);

        var device = DescribeUniFiDevice(port);
        var network = GetNetwork(port.NativeNetworkId, networks);
        var metadata = new Dictionary<string, object>
        {
            { "network", network?.Name ?? "Unknown" },
            { "device", device },
            { "device_type", port.ConnectedDeviceType ?? port.ConnectedClient?.UniFiProductLine ?? "unknown" }
        };

        // Already MAC-restricted: secured, so no score impact. The lock is offered as the simpler tool,
        // except where the list admits several devices; a lock would block the others.
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false))
        {
            if ((port.AllowedMacAddresses?.Count ?? 0) > 1 || blockedByProfile)
                return null;

            return CreateIssue(
                $"Port is MAC-restricted for {device}; Lock Port to UniFi Device can replace the MAC list",
                port,
                metadata,
                $"This port already restricts by MAC address. Lock Port to UniFi Device ties the port to {device} " +
                "as a single port setting instead of a MAC list. If you prefer it, enable Lock Port to UniFi Device " +
                "on this port in UniFi Network - Ports.",
                overrideSeverity: AuditSeverity.Informational,
                overrideScoreImpact: 0);
        }

        // A profile blocks the lock. MacRestrictionRule already covers an endpoint on an access port; an AP,
        // switch, bridge, or gateway, or any trunk, has no other port security suggestion, so offer the lock
        // as an option. Info only: the profile is a legitimate choice for same-role ports.
        if (blockedByProfile)
        {
            if (IsAccessPort(port) && !IsNetworkFabricDevice(port.ConnectedDeviceType))
                return null;

            // The lock needs UniFi Network 10.6.101+, so this copy always uses the 10.6 setting names
            var profile = string.IsNullOrEmpty(port.AssignedPortProfile?.Name)
                ? "its Port Profile"
                : $"the \"{port.AssignedPortProfile.Name}\" Port Profile";
            return CreateIssue(
                $"Port could be locked to {device} if {profile} is removed",
                port,
                metadata,
                "Lock Port to UniFi Device can't be enabled on a port that uses a Port Profile. " +
                "If you'd rather lock this port than share the profile with other ports, remove the profile in UniFi Network - Ports, " +
                "then enable Lock Port to UniFi Device.",
                overrideSeverity: AuditSeverity.Informational,
                overrideScoreImpact: 0);
        }

        // An AP or switch downlink carries its clients' MACs too, which the lock does not block
        var recommendation = IsNetworkFabricDevice(port.ConnectedDeviceType)
            ? "In UniFi Network - Ports, open this port and enable Lock Port to UniFi Device. " +
              $"A different device plugged into this port in its place is blocked; clients connected through {device} are not affected."
            : $"Only {device} has used this port. In UniFi Network - Ports, open this port and enable Lock Port to UniFi Device. " +
              "A different device plugged into this port in its place is blocked, with no MAC list to maintain.";

        return CreateIssue(
            $"Port should be locked to {device} with Lock Port to UniFi Device",
            port,
            metadata,
            recommendation);
    }
}
