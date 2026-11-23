using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Passthrough VLANs (Network 11.1+): a WAN that bridges ISP VLANs straight to internal VLAN Only Networks. UniFi's
/// own warning: traffic passed through from the ISP is not filtered by the firewall.
/// </summary>
[VendorSpecific("UniFi", "WAN networkconf tagged_networkconf_ids naming purpose vlan-only networks (Network 11.1)")]
public static class WanPassthroughAnalyzer
{
    /// <summary>One issue per WAN with Passthrough VLANs; none on consoles before 11.1, which never set the field.</summary>
    public static List<AuditIssue> Analyze(IReadOnlyList<UniFiNetworkConfig>? networks, string gatewayName = "Gateway")
    {
        var issues = new List<AuditIssue>();
        if (networks is not { Count: > 0 }) return issues;
        var byId = networks.Where(n => n.Id.Length > 0).GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var wan in networks.Where(n => string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase)))
        {
            if (wan.TaggedNetworkconfIds is not { Count: > 0 } ids) continue;
            var passed = ids.Select(id => byId.GetValueOrDefault(id)).OfType<UniFiNetworkConfig>()
                .Select(n => n.Vlan is { } vlan ? $"VLAN {vlan} ({n.Name})" : n.Name).ToList();
            if (passed.Count == 0) continue;
            var list = string.Join(", ", passed);
            var wanName = wan.Name.Length > 0 ? wan.Name : wan.WanNetworkgroup ?? "WAN";

            issues.Add(new AuditIssue
            {
                Type = IssueTypes.WanPassthroughVlan,
                Severity = AuditSeverity.Recommended,
                Message = $"{wanName} passes ISP traffic straight to {list}. UniFi does not filter passthrough traffic with the firewall.",
                DeviceName = gatewayName,
                Metadata = new Dictionary<string, object>
                {
                    ["wan"] = wanName,
                    ["passthrough_networks"] = passed,
                },
                RuleId = "WAN-PASSTHROUGH-001",
                ScoreImpact = 5,
                RecommendedAction = "Keep each passthrough VLAN to the ISP service it carries (IPTV, VoIP), with its VLAN Only Network tagged only on the ports that service's equipment uses.",
            });
        }
        return issues;
    }
}
