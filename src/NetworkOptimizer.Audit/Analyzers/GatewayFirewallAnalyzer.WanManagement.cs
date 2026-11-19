using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Audit.Analyzers;

public partial class GatewayFirewallAnalyzer
{
    private const string WanLocalChain = "UBIOS_WAN_LOCAL_USER";

    /// <summary>UniFi's port set for Direct Remote Connection; it holds 443 only.</summary>
    private const string ConsoleHttpsPortSet = "UBIOS_unifi_console_https_port";

    /// <summary>
    /// Management ports the gateway accepts new TCP connections on from the WAN (<c>UBIOS_WAN_LOCAL_USER</c>), and a
    /// finding for those no firewall policy accounts for. <paramref name="explainedPorts"/> are the ports the policy check
    /// already reported; they are returned as accepted but raise nothing here.
    /// </summary>
    /// <remarks>
    /// A rule matching a port set other than <see cref="ConsoleHttpsPortSet"/> is skipped: the set's members are not in
    /// the snapshot, and a policy's own port set is the policy check's to judge.
    /// </remarks>
    [VendorSpecific("UniFi", "UBIOS_WAN_LOCAL_USER chain and the UBIOS_unifi_console_https_port set")]
    public (List<AuditIssue> Issues, IReadOnlySet<string> AcceptedPorts) AnalyzeWanManagementAccess(
        GatewayFirewallSnapshot snapshot, IReadOnlySet<string> explainedPorts)
    {
        var v4 = AcceptedManagementPorts(IptablesDump.Parse(snapshot.Iptables));
        var v6 = AcceptedManagementPorts(IptablesDump.Parse(snapshot.Ip6tables));
        var accepted = new HashSet<string>(v4.Concat(v6), StringComparer.Ordinal);

        var unexplained = WanGatewayExposureAnalyzer.ManagementPorts
            .Where(p => accepted.Contains(p) && !explainedPorts.Contains(p)).ToList();
        if (unexplained.Count == 0)
            return ([], accepted);

        var ipv6Only = !unexplained.Any(v4.Contains);
        var ports = string.Join(", ", unexplained);
        _logger.LogInformation("Gateway firewall: the WAN input chain accepts TCP {Ports} with no policy behind it", ports);
        var issue = new AuditIssue
        {
            Type = IssueTypes.GatewayWanManagementExposed,
            Severity = AuditSeverity.Critical,
            Message = $"The gateway accepts TCP {ports} from the internet, and no firewall policy in UniFi Network accounts for it{IpFamilyText.Suffix(ipv6Only)}",
            RecommendedAction = "Find what adds this rule on the gateway, such as an edit over SSH or a boot script. UniFi Network neither shows nor manages it.",
            Metadata = IpFamilyText.Tag(new Dictionary<string, object>
            {
                { WanGatewayExposureAnalyzer.SourceKey, WanGatewayExposureAnalyzer.GatewaySource },
                { WanGatewayExposureAnalyzer.PortsKey, ports }
            }, ipv6Only),
            RuleId = WanGatewayExposureAnalyzer.RuleId,
            ScoreImpact = WanGatewayExposureAnalyzer.AnySourceScoreImpact
        };
        return ([issue], accepted);
    }

    private static HashSet<string> AcceptedManagementPorts(IptablesDump dump)
    {
        var ports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in dump.RulesOf(WanLocalChain))
        {
            if (!rule.EndsWith(" -j ACCEPT", StringComparison.Ordinal))
                continue;
            if (ProtocolRegex().Match(rule) is { Success: true } p && p.Groups["p"].Value is not ("tcp" or "6"))
                continue;
            if (StateRegex().Match(rule) is { Success: true } s && !s.Groups["s"].Value.Split(',').Contains("NEW"))
                continue;
            if (CoveredPorts(rule) is { } covered)
                ports.UnionWith(covered);
        }
        return ports;
    }

    /// <summary>The management ports a rule's destination port match covers; null when it cannot be told.</summary>
    private static IEnumerable<string>? CoveredPorts(string rule)
    {
        if (rule.Contains("! --dport", StringComparison.Ordinal) || NegatedDstSetRegex().IsMatch(rule))
            return null;

        var specs = new List<string>();
        if (DportRegex().Match(rule) is { Success: true } single) specs.Add(single.Groups["spec"].Value);
        if (DportsRegex().Match(rule) is { Success: true } multi) specs.AddRange(multi.Groups["spec"].Value.Split(','));
        foreach (Match set in DstSetRegex().Matches(rule))
        {
            var name = set.Groups["set"].Value;
            if (name == ConsoleHttpsPortSet) specs.Add("443");
            else if (name.Contains("port", StringComparison.OrdinalIgnoreCase)) return null;
        }

        if (specs.Count == 0)
            return WanGatewayExposureAnalyzer.ManagementPorts;
        return WanGatewayExposureAnalyzer.ManagementPorts.Where(port => specs.Any(spec => InPortSpec(spec, int.Parse(port))));
    }

    private static bool InPortSpec(string spec, int port)
    {
        var range = spec.Split(':');
        if (range.Length == 2 && int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi))
            return port >= lo && port <= hi;
        return int.TryParse(spec, out var single) && single == port;
    }

    [GeneratedRegex(@" -p (?<p>\S+)")]
    private static partial Regex ProtocolRegex();

    [GeneratedRegex(@" --(?:ct)?state (?<s>\S+)")]
    private static partial Regex StateRegex();

    [GeneratedRegex(@" --dport (?<spec>\d+(?::\d+)?)")]
    private static partial Regex DportRegex();

    [GeneratedRegex(@" --dports (?<spec>[\d,:]+)")]
    private static partial Regex DportsRegex();

    [GeneratedRegex(@"(?<!! )--match-set (?<set>\S+) dst")]
    private static partial Regex DstSetRegex();

    [GeneratedRegex(@"! --match-set \S+ dst")]
    private static partial Regex NegatedDstSetRegex();
}
