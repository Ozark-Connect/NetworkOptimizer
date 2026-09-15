using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Rules;

/// <summary>
/// Detects access ports without MAC address restrictions.
/// MAC restrictions help prevent unauthorized device connections.
/// Excludes infrastructure ports (uplinks, WAN, ports with network fabric devices connected)
/// and ports locked with Lock Port to UniFi Device. Ports carrying another UniFi device
/// (Protect, Cloud Key, ...) defer to PortLockRule when the lock is available, unless several
/// clients used the port recently; then this rule keeps the issue and names the lock as the
/// single-device alternative.
/// </summary>
public class MacRestrictionRule : AuditRuleBase
{
    public override string RuleId => "MAC-RESTRICT-001";
    public override string RuleName => "MAC Address Restriction";
    public override string Description => "Access ports should have MAC restrictions to prevent unauthorized devices";
    public override AuditSeverity Severity => AuditSeverity.Recommended;
    public override int ScoreImpact => 3;

    public override AuditIssue? Evaluate(PortInfo port, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        // Skip ports that are down AND have no recent activity.
        // Down ports with recent connections should still be checked - the unused port rule
        // won't flag them (within grace period), so MAC restriction is the right recommendation.
        // Truly inactive ports (no recent connection) will be caught by the unused port rule instead.
        if (!port.IsUp && !port.LastConnectionSeen.HasValue)
            return null;

        // Check if this is an access port (native or custom with native network set)
        if (!IsAccessPort(port))
            return null;

        // Skip infrastructure ports
        if (port.IsUplink || port.IsWan)
            return null;

        // Skip mirror destination ports. Defense-in-depth: the isAccessPort check below
        // already filters them since mirror destinations have forward="all" (not an access
        // port shape), but the explicit guard makes the design intent clear.
        if (port.IsMirrorDestination)
            return null;

        // Skip ports with network fabric devices (AP, switch, bridge) - these are LAN infrastructure
        // Modems, NVRs, Cloud Keys, etc. are endpoints and SHOULD get MAC restriction recommendations
        if (IsNetworkFabricDevice(port.ConnectedDeviceType))
            return null;

        // Fallback: check if port name suggests an AP (for cases where uplink data isn't available)
        if (IsAccessPointName(port.Name))
            return null;

        // Check if switch supports MAC ACLs
        if (port.Switch.Capabilities.MaxCustomMacAcls == 0)
            return null; // Switch doesn't support this feature

        var network = GetNetwork(port.NativeNetworkId, networks);

        // Server networks: servers/hypervisors have multiple MACs from VMs and containers,
        // so MAC restriction is impractical. Recommend 802.1X multi-host if the switch supports it,
        // otherwise skip entirely.
        if (network?.Purpose == NetworkPurpose.Server)
        {
            if (port.IsDot1xSecured)
                return null; // Already secured via RADIUS

            if (port.Switch.Capabilities.Dot1xPortCtrlEnabled)
            {
                return CreateIssue(
                    "Server port should use 802.1X authentication for port security",
                    port,
                    new Dictionary<string, object>
                    {
                        { "network", network.Name }
                    },
                    "This port is on a Server network where MAC restriction is impractical due to multiple VM/container MACs. " +
                    "Use 802.1X Multi-Host mode to authenticate the server, then allow subsequent MACs on the port.");
            }

            return null; // Switch doesn't support 802.1X, nothing actionable
        }

        // Check if port already has MAC restrictions
        if (port.PortSecurityEnabled || (port.AllowedMacAddresses?.Any() ?? false))
            return null; // Already has restrictions

        // Skip ports secured via 802.1X/RADIUS authentication
        if (port.IsDot1xSecured)
            return null; // Already secured via RADIUS

        // Locked to a UniFi device (Lock Port to UniFi Device) - the lock is the restriction
        if (port.IsPortLocked)
            return null;

        // Check if port has an intentional unrestricted profile assigned
        // (user has created an access port profile with MAC restriction explicitly disabled)
        if (HasIntentionalUnrestrictedProfile(port))
            return null;

        // Tailor the message based on whether the port is actively in use or just recently used
        var isInactive = !port.IsUp;

        // A UniFi device on a port several clients used: MAC restriction is the tool, the lock only if the
        // device turns out to be the sole occupant. PortLockRule stays silent here so this carries the score.
        if (!isInactive && port.HasUniFiDevice && port.IsSharedPort)
        {
            // Without the lock on offer, the plain MAC restriction copy below fits a shared port as-is
            if (IsPortLockAvailable(port))
            {
                var device = DescribeUniFiDevice(port);
                var count = port.SeenDeviceMacs.Count;
                return CreateIssue(
                    $"Port carries {device} but {count} devices have used it; Lock Port to UniFi Device fits only if {device} is the sole occupant",
                    port,
                    new Dictionary<string, object>
                    {
                        { "network", network?.Name ?? "Unknown" },
                        { "device", device },
                        { "devices_seen", count }
                    },
                    $"{count} different devices have used this port in the last {Analyzers.PortSecurityAnalyzer.SharedPortWindowDays} days. If more than one device needs it, " +
                    "use Restricted with each allowed MAC address instead. " +
                    $"If {device} is the only one that belongs here, enable Lock Port to UniFi Device in Port Manager.");
            }
        }
        // A UniFi device (Protect, Network, ...) on the port: Lock Port to UniFi Device is the better fit.
        // PortLockRule flags it when the versions allow; otherwise say what the upgrade would unlock.
        // A LAG, or a port on a gateway or AP, never takes the lock, so it gets the plain copy below
        // rather than an upgrade it cannot use.
        else if (!isInactive && port.HasUniFiDevice && CanPortEverLock(port))
        {
            if (IsPortLockAvailable(port))
                return null;

            var device = DescribeUniFiDevice(port);
            var blockedByProfile = IsPortLockSupported(port) && IsPortLockBlockedByProfile(port);
            var lockNote = blockedByProfile
                ? $"This port carries {device}. Lock Port to UniFi Device would tie the port to that device, but the lock cannot be " +
                  "combined with an Ethernet Port Profile. Either remove the profile and lock the port, or keep the profile and " +
                  "set the port to 'Restricted' with the device's MAC address in the allowed list."
                : $"This port carries {device}. Lock Port to UniFi Device ties the port to that device and needs UniFi Network " +
                  $"{PortLockSupport.MinNetworkApplicationVersion} or newer and switch firmware {PortLockSupport.MinSwitchFirmwareVersion} or newer. " +
                  "Until then, set the port to 'Restricted' and add the device's MAC address to the allowed list.";
            return CreateIssue(
                blockedByProfile
                    ? $"Port should be set to Restricted w/ an Allowed MAC Address for {device}, or have its Ethernet Port Profile removed and be locked to it with Lock Port to UniFi Device"
                    : $"Port should be set to Restricted w/ an Allowed MAC Address for {device}, or locked to it once Lock Port to UniFi Device is available",
                port,
                new Dictionary<string, object>
                {
                    { "network", network?.Name ?? "Unknown" },
                    { "device", device }
                },
                lockNote);
        }

        var message = isInactive
            ? "Port is not in use - disable it, or add a MAC restriction if it's still needed"
            : "Port should be set to Restricted w/ an Allowed MAC Address or restricted via an Ethernet Port Profile in UniFi Network";

        var recommendation = isInactive
            ? "This port has no active connection. If it's no longer needed, set it to 'Disabled' in UniFi to prevent unauthorized access. " +
              "If it's still in use periodically, set it to 'Restricted' and add the device's MAC address to the allowed list."
            : "Enable MAC-based port security to prevent unauthorized devices from connecting. " +
              "In UniFi, set the port to 'Restricted' and add the device's MAC address to the allowed list. " +
              "If this port is intended to be used by multiple devices, create an Ethernet Port Profile with MAC restriction disabled and assign it to this port.";

        return CreateIssue(
            message,
            port,
            new Dictionary<string, object>
            {
                { "network", network?.Name ?? "Unknown" }
            },
            recommendation);
    }

}
