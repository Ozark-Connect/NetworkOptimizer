using System.Text.Json;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Finds the gateway's management services reachable from the internet: an External -> Gateway allow on a
/// management port, including the predefined one UniFi adds when Direct Remote Connection is on.
/// </summary>
/// <remarks>
/// Zone-based firewalls only. Legacy WAN_LOCAL rules carry no zone, so only the setting is checked there.
/// The live half (what the gateway's iptables accept) is <see cref="GatewayFirewallAnalyzer"/>'s.
/// </remarks>
[VendorSpecific("UniFi", "Direct Remote Connection: mgmt.direct_connect_enabled and its predefined policy's origin_type")]
public static class WanGatewayExposureAnalyzer
{
    public const string RuleId = "FW-GATEWAY-WAN";
    public const int AnySourceScoreImpact = 25;
    public const int LimitedSourceScoreImpact = 7;

    /// <summary>
    /// TCP ports of the gateway's management services: SSH, the web UI, the Network app GUI, the Protect API, and the
    /// Network app's packet capture and support file downloads. 8444 is not one: it serves only the Hotspot portal.
    /// </summary>
    public static readonly IReadOnlyList<string> ManagementPorts = ["22", "80", "443", "8443", "7443", "28082"];

    /// <summary>Metadata: what opens the port (<see cref="DirectRemoteConnectionSource"/>, <see cref="RuleSource"/>, <see cref="GatewaySource"/>).</summary>
    public const string SourceKey = "exposure_source";
    public const string DirectRemoteConnectionSource = "direct_remote_connection";
    public const string RuleSource = "rule";
    public const string GatewaySource = "gateway";

    /// <summary>Metadata: the exposed ports, comma separated.</summary>
    public const string PortsKey = "ports";

    /// <summary>Metadata set on a config finding when the gateway's iptables accept the same ports.</summary>
    public const string EnforcedKey = "enforced_on_gateway";

    private const string DirectRemoteConnectionOrigin = "console_setting_direct_connection";
    private const string DirectRemoteConnectionPolicyName = "Allow Direct Remote Connection";

    /// <summary>
    /// The exposure findings for these policies. <paramref name="directRemoteConnectionEnabled"/> raises the Direct Remote
    /// Connection finding when its policy is missing from <paramref name="rules"/>, so the finding does not depend on that read.
    /// </summary>
    /// <param name="rules">Firewall policies, predefined ones included.</param>
    /// <param name="externalZoneId">External zone ID; null skips the policy checks.</param>
    /// <param name="gatewayZoneId">Gateway zone ID; null skips the policy checks.</param>
    /// <param name="directRemoteConnectionEnabled">The site's mgmt.direct_connect_enabled.</param>
    public static List<AuditIssue> Analyze(IReadOnlyList<FirewallRule> rules, string? externalZoneId, string? gatewayZoneId,
        bool directRemoteConnectionEnabled)
    {
        var issues = new List<AuditIssue>();
        var policyKnown = false;

        if (!string.IsNullOrEmpty(externalZoneId) && !string.IsNullOrEmpty(gatewayZoneId))
        {
            policyKnown = rules.Any(r => r.Enabled && IsDirectRemoteConnectionPolicy(r));
            foreach (var exposure in FindExposures(rules, externalZoneId, gatewayZoneId))
                issues.Add(ToIssue(exposure, directRemoteConnectionEnabled));
        }

        if (directRemoteConnectionEnabled && !policyKnown)
            issues.Add(DirectRemoteConnectionIssue([ "443" ], ipv6Only: false, fromSettingOnly: true));

        return issues;
    }

    /// <summary>Reads mgmt.direct_connect_enabled from rest/setting. Absent means it was never turned on.</summary>
    public static bool ReadDirectRemoteConnection(JsonElement? settings)
    {
        if (settings is not { } data || data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return false;
        return data.UnwrapDataArray()
            .Where(s => s.ValueKind == JsonValueKind.Object && s.GetStringOrNull("key") == "mgmt")
            .Any(s => s.GetBoolOrDefault("direct_connect_enabled"));
    }

    private sealed class Exposure(FirewallRule rule, bool anySource)
    {
        public FirewallRule Rule { get; } = rule;
        public bool AnySource { get; } = anySource;
        public List<string> Ports { get; } = [];
        public bool OverIpv4 { get; set; }
    }

    /// <summary>
    /// Per family and port, the winning External -> Gateway policy for any internet host, then for some host.
    /// A block decides only when it covers every source and every gateway address; an allow decides however it is narrowed.
    /// </summary>
    private static List<Exposure> FindExposures(IReadOnlyList<FirewallRule> rules, string externalZoneId, string gatewayZoneId)
    {
        var byRule = new Dictionary<string, Exposure>(StringComparer.Ordinal);
        foreach (var family in new[] { IpFamily.IPv4, IpFamily.IPv6 })
        {
            foreach (var port in ManagementPorts)
            {
                var anyHost = FirewallRuleEvaluator.Evaluate(rules,
                    r => Matches(r, externalZoneId, gatewayZoneId, port, anySourceOnly: true), forNewConnections: true, family: family);
                var winner = anyHost.IsAllowed ? anyHost.EffectiveRule : null;
                var anySource = winner != null;
                if (winner == null)
                {
                    var someHost = FirewallRuleEvaluator.Evaluate(rules,
                        r => Matches(r, externalZoneId, gatewayZoneId, port, anySourceOnly: false), forNewConnections: true, family: family);
                    winner = someHost.IsAllowed ? someHost.EffectiveRule : null;
                }
                if (winner == null)
                    continue;

                if (!byRule.TryGetValue(winner.Id, out var exposure))
                    byRule[winner.Id] = exposure = new Exposure(winner, anySource);
                if (!exposure.Ports.Contains(port))
                    exposure.Ports.Add(port);
                exposure.OverIpv4 |= family == IpFamily.IPv4;
            }
        }
        return byRule.Values.OrderBy(e => e.Rule.Index).ToList();
    }

    private static bool Matches(FirewallRule rule, string externalZoneId, string gatewayZoneId, string port, bool anySourceOnly)
    {
        if (!string.Equals(rule.SourceZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rule.DestinationZoneId, gatewayZoneId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (rule.WebDomains is { Count: > 0 } || rule.AppIds is { Count: > 0 } || rule.AppCategoryIds is { Count: > 0 })
            return false;
        if (!FirewallGroupHelper.RuleMatchesPortAndProtocol(rule, port, "tcp"))
            return false;

        if (rule.ActionType.IsBlockAction())
            return CoversEverySource(rule) && string.IsNullOrEmpty(rule.SourcePort) && !IsDestinationNarrowed(rule);
        return !anySourceOnly || CoversEverySource(rule);
    }

    /// <summary>ANY, or "every address except these" (Match Opposite on a source IP list).</summary>
    private static bool CoversEverySource(FirewallRule rule) =>
        string.IsNullOrEmpty(rule.SourceMatchingTarget) ||
        rule.SourceMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase) ||
        (rule.SourceMatchingTarget.Equals("IP", StringComparison.OrdinalIgnoreCase) && rule.SourceMatchOppositeIps);

    private static bool IsDestinationNarrowed(FirewallRule rule) =>
        !string.IsNullOrEmpty(rule.DestinationMatchingTarget) &&
        !rule.DestinationMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase);

    /// <summary>origin_id differs per install, so origin_type and the predefined name are the stable identity.</summary>
    private static bool IsDirectRemoteConnectionPolicy(FirewallRule rule) =>
        string.Equals(rule.OriginType, DirectRemoteConnectionOrigin, StringComparison.OrdinalIgnoreCase) ||
        (rule.Predefined && string.Equals(rule.Name, DirectRemoteConnectionPolicyName, StringComparison.OrdinalIgnoreCase));

    private static AuditIssue ToIssue(Exposure exposure, bool directRemoteConnectionEnabled)
    {
        var ipv6Only = !exposure.OverIpv4;
        if (IsDirectRemoteConnectionPolicy(exposure.Rule))
        {
            var issue = DirectRemoteConnectionIssue(exposure.Ports, ipv6Only, fromSettingOnly: false);
            issue.Metadata!["rule_name"] = exposure.Rule.Name ?? "";
            issue.Metadata["setting_enabled"] = directRemoteConnectionEnabled;
            return issue;
        }

        var rule = exposure.Rule.Name ?? exposure.Rule.Id;
        var ports = string.Join(", ", exposure.Ports);
        var suffix = IpFamilyText.Suffix(ipv6Only);
        var metadata = new Dictionary<string, object>
        {
            { SourceKey, RuleSource },
            { PortsKey, ports },
            { "rule_name", rule },
            { "rule_id", exposure.Rule.Id },
            { "any_source", exposure.AnySource }
        };

        if (exposure.AnySource)
        {
            return new AuditIssue
            {
                Type = IssueTypes.GatewayWanManagementExposed,
                Severity = AuditSeverity.Critical,
                Message = $"Rule '{rule}' lets any host on the internet reach the gateway's management on TCP {ports}{suffix}",
                RecommendedAction = $"Disable rule '{rule}', or limit its source to addresses you control. For remote management, use a VPN or Remote Access through unifi.ui.com.",
                Metadata = IpFamilyText.Tag(metadata, ipv6Only),
                RuleId = RuleId,
                ScoreImpact = AnySourceScoreImpact
            };
        }

        var source = DescribeSource(exposure.Rule);
        metadata["source"] = source;
        return new AuditIssue
        {
            Type = IssueTypes.GatewayWanManagementExposed,
            Severity = AuditSeverity.Recommended,
            Message = $"Rule '{rule}' lets {source} reach the gateway's management from the internet on TCP {ports}{suffix}",
            RecommendedAction = $"If this access is intended, keep {source} to addresses you control. A VPN is safer for remote management.",
            Metadata = IpFamilyText.Tag(metadata, ipv6Only),
            RuleId = RuleId,
            ScoreImpact = LimitedSourceScoreImpact
        };
    }

    private static AuditIssue DirectRemoteConnectionIssue(IReadOnlyList<string> ports, bool ipv6Only, bool fromSettingOnly) => new()
    {
        Type = IssueTypes.GatewayWanManagementExposed,
        Severity = AuditSeverity.Critical,
        Message = $"Direct Remote Connection is on, so any host on the internet can reach the console sign-in page on TCP 443{IpFamilyText.Suffix(ipv6Only)}",
        RecommendedAction = "Turn off Direct Remote Connection in Management (or Control Plane) - Advanced. Remote Access through unifi.ui.com keeps working without it.",
        Metadata = IpFamilyText.Tag(new Dictionary<string, object>
        {
            { SourceKey, DirectRemoteConnectionSource },
            { PortsKey, string.Join(", ", ports) },
            { "from_setting_only", fromSettingOnly }
        }, ipv6Only),
        RuleId = RuleId,
        ScoreImpact = AnySourceScoreImpact
    };

    /// <summary>The limited source as the rule shows it: a few addresses by value, more by count, or regions.</summary>
    private static string DescribeSource(FirewallRule rule)
    {
        if (string.Equals(rule.SourceMatchingTarget, "IP", StringComparison.OrdinalIgnoreCase) && rule.SourceIps is { Count: > 0 } ips)
            return ips.Count <= 3 ? string.Join(", ", ips) : $"{ips.Count} source addresses";
        if (string.Equals(rule.SourceMatchingTarget, "REGION", StringComparison.OrdinalIgnoreCase))
            return "sources in the selected regions";
        return "a limited set of sources";
    }
}
