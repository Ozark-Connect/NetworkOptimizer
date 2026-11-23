using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Port Automations (Network 11.1+) that put a device on a trusted network by its vendor or MAC OUI. Both are claims
/// the device makes about itself: a MAC OUI is set in software, and vendor detection reads the device's own traffic.
/// </summary>
[VendorSpecific("UniFi", "v2 port-automations match.vendor_ids / match.mac_ouis and the profile's native network")]
public static class PortAutomationAnalyzer
{
    /// <summary>Networks a device should not reach by claiming an identity.</summary>
    private static readonly HashSet<NetworkPurpose> Trusted =
    [
        NetworkPurpose.Management, NetworkPurpose.ManagementAccess, NetworkPurpose.Corporate, NetworkPurpose.Home, NetworkPurpose.Server,
    ];

    /// <summary>One issue per enabled automation whose profile's native network is trusted; none before 11.1.</summary>
    public static List<AuditIssue> Analyze(IReadOnlyList<UniFiPortAutomation>? automations, IReadOnlyList<UniFiPortProfile>? profiles,
        IReadOnlyList<NetworkInfo> networks, string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();
        if (automations is not { Count: > 0 } || profiles is not { Count: > 0 }) return issues;

        foreach (var automation in automations.Where(a => a.Enabled))
        {
            var byVendor = automation.Match?.VendorIds is { Count: > 0 };
            var byOui = automation.Match?.MacOuis is { Count: > 0 };
            if (!byVendor && !byOui) continue;
            var profile = profiles.FirstOrDefault(p => string.Equals(p.Id, automation.PortProfileId, StringComparison.OrdinalIgnoreCase));
            var network = profile?.NativeNetworkId is { } nid
                ? networks.FirstOrDefault(n => string.Equals(n.Id, nid, StringComparison.OrdinalIgnoreCase))
                : null;
            if (profile == null || network == null || !Trusted.Contains(network.Purpose)) continue;

            var how = byVendor && byOui ? "vendor or MAC OUI" : byVendor ? "vendor" : "MAC OUI";
            var management = network.Purpose is NetworkPurpose.Management or NetworkPurpose.ManagementAccess;
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.PortAutomationTrustedNetwork,
                Severity = AuditSeverity.Recommended,
                Message = $"Port Automation {automation.Name ?? automation.Id} puts any device that matches its {how} on {network.Name} "
                    + $"(Port Profile {profile.Name}). A device can claim a {how} it does not have.",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["port_automation"] = automation.Name ?? "",
                    ["port_profile"] = profile.Name,
                    ["network"] = network.Name,
                    ["match"] = how,
                },
                RuleId = "PORT-AUTOMATION-001",
                ScoreImpact = management ? 8 : 5,
                RecommendedAction = "Point the automation at a restricted network (IoT, cameras), and set ports on trusted networks by hand.",
            });
        }
        return issues;
    }
}
