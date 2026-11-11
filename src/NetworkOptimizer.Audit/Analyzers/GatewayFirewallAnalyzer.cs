using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Checks the gateway enforces what UniFi Network says it should: each interface sits in its zone, each
/// enabled policy reached the chain for its zone pair, and Threat Intelligence's gateway agent blocking is in
/// place. It checks shape and wiring, never each rule's match fields or each blocklist entry.
/// </summary>
/// <remarks>
/// UniFi compiles zone policies into chains named <c>UBIOS_&lt;SRC&gt;_&lt;DST&gt;_USER</c> and dispatches each
/// interface to its zone in <c>UBIOS_FORWARD_IN_USER</c> and <c>UBIOS_INPUT_USER_HOOK</c>. The live udapi filter
/// tags every compiled rule with its policy ID (<c>triggerTag.id</c>), which links the controller to the gateway.
/// </remarks>
[VendorSpecific("UniFi", "udapi firewall filter, UBIOS chain and zone token names")]
public partial class GatewayFirewallAnalyzer
{
    public const int WanNotExternalScoreImpact = 10;
    public const int NetworkZoneScoreImpact = 5;
    public const int PolicyNotEnforcedScoreImpact = 5;
    public const int DisabledPolicyEnforcedScoreImpact = 3;
    public const int ZonePairUnwiredScoreImpact = 5;
    public const int BlocklistHookMissingScoreImpact = 8;
    public const int BlocklistChainScoreImpact = 3;
    public const int BlocklistSetScoreImpact = 5;

    // A current agent repairs its hooks, chain, and sets on its own, so a finding that stays points at an older agent.
    // Upgrade appears in Settings - Multi-Site only for an agent behind the latest release, hence the restart fallback.
    private const string RestartAgentAction =
        "Upgrade the On-Site Agent on the gateway from Settings - Multi-Site. If it is already current, restart it over SSH: systemctl restart netopt-agent.";

    /// <summary>Metadata key naming the page that fixes a finding; the Web layer turns it into a link.</summary>
    public const string ActionKey = "action";

    /// <summary><see cref="ActionKey"/> value: upgrade the site's On-Site Agent.</summary>
    public const string UpgradeAgentAction = "upgrade_agent";

    /// <summary>Most names listed in one finding's message; the rest are counted.</summary>
    internal const int MaxListed = 8;

    private static readonly Dictionary<string, string> FixedZoneTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["internal"] = "LAN",
        ["external"] = "WAN",
        ["gateway"] = "LOCAL",
        ["vpn"] = "VPN",
        ["hotspot"] = "GUEST",
        ["dmz"] = "DMZ",
    };

    private readonly ILogger<GatewayFirewallAnalyzer> _logger;

    public GatewayFirewallAnalyzer(ILogger<GatewayFirewallAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compare the gateway's firewall state with UniFi Network's configuration.
    /// </summary>
    /// <param name="snapshot">What the gateway holds.</param>
    /// <param name="policies">The parsed firewall policies.</param>
    /// <param name="zones">The firewall zones; null or empty skips the zone checks (a legacy firewall).</param>
    /// <param name="networks">The network configs.</param>
    /// <param name="wanInterfaces">Each WAN's data-path interface on the gateway.</param>
    /// <param name="blocklist">What Threat Intelligence's gateway agent should have in place; null when the site does not block on its gateway.</param>
    /// <param name="gatewayName">The gateway's name, for the findings.</param>
    public List<AuditIssue> Analyze(
        GatewayFirewallSnapshot snapshot,
        IReadOnlyList<FirewallRule>? policies,
        IReadOnlyList<UniFiFirewallZone>? zones,
        IReadOnlyList<UniFiNetworkConfig>? networks,
        IReadOnlyList<GatewayWanPath>? wanInterfaces,
        GatewayBlocklistExpectation? blocklist,
        string? gatewayName)
    {
        var issues = new List<AuditIssue>();
        var v4 = IptablesDump.Parse(snapshot.Iptables);
        var v6 = IptablesDump.Parse(snapshot.Ip6tables);

        if (zones is { Count: > 0 } && v4.HasRules)
        {
            var udapi = UdapiFilter.Parse(snapshot.UdapiFilterJson, _logger);
            var tokens = ResolveZoneTokens(zones, policies, udapi, networks, v4);
            AddWanZoneIssues(issues, v4, zones, networks, wanInterfaces, gatewayName);
            AddNetworkZoneIssues(issues, v4, zones, networks, tokens, gatewayName);
            if (udapi != null)
                AddPolicyIssues(issues, udapi, v4, policies, networks, tokens, gatewayName, _logger);
        }

        if (blocklist != null && v4.HasRules)
            AddBlocklistIssues(issues, blocklist, v4, v6, IpsetHeaders.Parse(snapshot.IpsetHeaders), gatewayName);

        _logger.LogInformation("Gateway firewall: {Issues} issues ({Rules4} IPv4 and {Rules6} IPv6 rules read)",
            issues.Count, v4.RuleCount, v6.RuleCount);
        return issues;
    }

    // === Zones and interfaces ===

    private static void AddWanZoneIssues(List<AuditIssue> issues, IptablesDump v4, IReadOnlyList<UniFiFirewallZone> zones,
        IReadOnlyList<UniFiNetworkConfig>? networks, IReadOnlyList<GatewayWanPath>? wanInterfaces, string? gatewayName)
    {
        var wrong = new List<string>();
        var enforced = new List<string>();
        var wrongNames = new List<string>();
        foreach (var wan in wanInterfaces ?? [])
        {
            var forward = v4.ForwardZones(wan.Interface);
            var input = v4.InputZones(wan.Interface);
            if (forward.SetEquals(["WAN"]) && input.SetEquals(["WAN"]))
                enforced.Add(wan.NetworkName);
            else
            {
                wrong.Add($"{wan.NetworkName} ({wan.Interface})");
                wrongNames.Add(wan.NetworkName);
            }
        }

        if (wrong.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayWanNotExternal,
                Severity = AuditSeverity.Critical,
                DeviceName = gatewayName,
                Message = $"The gateway does not treat {Join(wrong)} as part of the External zone. Policies to and from the internet do not apply to {(wrong.Count == 1 ? "it" : "them")}.",
                RecommendedAction = "Check the WAN in UniFi Network - Settings - Internet, then save it again so the gateway reprovisions. If the finding remains, restart the gateway.",
                RuleId = "GW-FW-001",
                ScoreImpact = WanNotExternalScoreImpact,
                Metadata = new() { ["wan_interfaces"] = wrong },
            });
        }

        // UniFi Network's own record of a WAN can name a zone that does not exist; the gateway decides what holds.
        var zoneIds = zones.Select(z => z.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dangling = (networks ?? [])
            .Where(n => IsWan(n) && !string.IsNullOrEmpty(n.FirewallZoneId) && !zoneIds.Contains(n.FirewallZoneId!))
            .ToList();
        if (dangling.Count > 0)
        {
            var names = dangling.Select(n => n.Name).ToList();
            // Only WANs the gateway carries were checked; an unused WAN profile has no interface there and says nothing.
            var held = names.Where(enforced.Contains).ToList();
            var notHeld = names.Where(wrongNames.Contains).ToList();
            var verdict = notHeld.Count > 0
                ? $" See the WAN Not in External Zone finding for {Join(notHeld)}."
                : held.Count > 0
                    ? $" The gateway still treats {Join(held)} as part of the External zone, so your policies are enforced."
                    : "";
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayWanZoneReference,
                Severity = AuditSeverity.Informational,
                DeviceName = gatewayName,
                Message = $"UniFi Network's settings for {Join(names)} point at a firewall zone that does not exist ({dangling[0].FirewallZoneId})." + verdict,
                RecommendedAction = "Nothing to change in Network Optimizer. The WAN zone assignment is locked in UniFi Network, so report it to Ubiquiti if it persists.",
                RuleId = "GW-FW-006",
                ScoreImpact = 0,
                Metadata = new() { ["wan_networks"] = names, ["zone_id"] = dangling[0].FirewallZoneId! },
            });
        }
    }

    private static void AddNetworkZoneIssues(List<AuditIssue> issues, IptablesDump v4, IReadOnlyList<UniFiFirewallZone> zones,
        IReadOnlyList<UniFiNetworkConfig>? networks, ZoneTokens tokens, string? gatewayName)
    {
        var wrong = new List<string>();
        foreach (var network in networks ?? [])
        {
            if (!network.Enabled || !IsLan(network)) continue;
            var zone = zones.FirstOrDefault(z => string.Equals(z.Id, network.FirewallZoneId, StringComparison.OrdinalIgnoreCase))
                       ?? zones.FirstOrDefault(z => z.NetworkIds.Contains(network.Id, StringComparer.OrdinalIgnoreCase));
            // Only a token learned from the zone's policies checks the network; one learned from its interface would pass itself.
            if (zone == null || tokens.Of(zone.Id, includeInterfaceGuesses: false) is not { } expected) continue;
            var bridge = BridgeOf(network);
            var actual = v4.ForwardZones(bridge);
            if (actual.Count == 0) continue;
            if (!actual.SetEquals([expected]))
                wrong.Add($"{network.Name} (UniFi Network: {zone.Name})");
        }

        if (wrong.Count == 0) return;
        issues.Add(new AuditIssue
        {
            Type = IssueTypes.GatewayNetworkZoneMismatch,
            Severity = AuditSeverity.Recommended,
            DeviceName = gatewayName,
            Message = $"The gateway enforces {Join(wrong)} in a different zone than UniFi Network shows, so {(wrong.Count == 1 ? "its" : "their")} traffic follows another zone's policies.",
            RecommendedAction = "Open the network in UniFi Network and save it again so the gateway reprovisions.",
            RuleId = "GW-FW-002",
            ScoreImpact = NetworkZoneScoreImpact,
            Metadata = new() { ["networks"] = wrong },
        });
    }

    // === Policies ===

    private static void AddPolicyIssues(List<AuditIssue> issues, UdapiFilter udapi, IptablesDump v4,
        IReadOnlyList<FirewallRule>? policies, IReadOnlyList<UniFiNetworkConfig>? networks, ZoneTokens tokens, string? gatewayName, ILogger logger)
    {
        var missing = new List<string>();
        var stillOn = new List<string>();
        foreach (var policy in policies ?? [])
        {
            if (policy.Predefined || !Guid.TryParse(policy.Id, out _)) continue;
            var chains = udapi.ChainsOf(policy.Id);
            if (!policy.Enabled)
            {
                if (chains.Count > 0) stillOn.Add(policy.Name ?? policy.Id);
                continue;
            }
            if (tokens.Of(policy.SourceZoneId) is not { } src || tokens.Of(policy.DestinationZoneId) is not { } dst) continue;
            var chain = $"{src}_{dst}";
            if (!chains.Contains(chain) && !udapi.TakeUntaggedRuleNamed(chain, policy.Name))
            {
                missing.Add(policy.Name ?? policy.Id);
                logger.LogDebug("Gateway firewall: policy '{Name}' ({Id}, {SrcZone} to {DstZone}, IP {Version}) expected in {Chain}; tagged in [{Tagged}], named in [{Named}]",
                    policy.Name, policy.Id, policy.SourceZoneId, policy.DestinationZoneId, policy.IpVersion ?? "both", chain,
                    string.Join(", ", chains), string.Join(", ", udapi.ChainsWithUntaggedRuleNamed(policy.Name)));
            }
        }

        if (missing.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayPolicyNotEnforced,
                Severity = AuditSeverity.Recommended,
                DeviceName = gatewayName,
                Message = $"{Count(missing.Count, "enabled firewall policy is", "enabled firewall policies are")} not on the gateway for {(missing.Count == 1 ? "its" : "their")} zones: {Join(missing)}.",
                RecommendedAction = "Save the policy again in UniFi Network so the gateway reprovisions. If it stays missing, restart the gateway.",
                RuleId = "GW-FW-003",
                ScoreImpact = PolicyNotEnforcedScoreImpact,
                Metadata = new() { ["policies"] = missing },
            });
        }

        if (stillOn.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayDisabledPolicyEnforced,
                Severity = AuditSeverity.Recommended,
                DeviceName = gatewayName,
                Message = $"{Count(stillOn.Count, "disabled firewall policy is", "disabled firewall policies are")} still enforced on the gateway: {Join(stillOn)}.",
                RecommendedAction = "Enable and disable the policy again in UniFi Network so the gateway reprovisions.",
                RuleId = "GW-FW-004",
                ScoreImpact = DisabledPolicyEnforcedScoreImpact,
                Metadata = new() { ["policies"] = stillOn },
            });
        }

        // A zone with no networks has no interface to dispatch, so its policy chains are unreached by design.
        var unwired = udapi.PolicyChains
            .Where(c => !v4.IsReachable($"UBIOS_{c}_USER") && !DispatchZoneIsEmpty(c, tokens, networks, v4))
            .Select(DisplayZonePair(tokens))
            .ToList();
        if (unwired.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayZonePairUnwired,
                Severity = AuditSeverity.Recommended,
                DeviceName = gatewayName,
                Message = $"The gateway holds policies for {Join(unwired)}, but no traffic reaches them.",
                RecommendedAction = "Restart the gateway so UniFi Network reprovisions its firewall.",
                RuleId = "GW-FW-005",
                ScoreImpact = ZonePairUnwiredScoreImpact,
                Metadata = new() { ["zone_pairs"] = unwired },
            });
        }
    }

    /// <summary>
    /// Whether the zone whose interfaces dispatch to the chain has no members: no interface on the gateway dispatches
    /// to it, and UniFi Network puts no network in it. A zone with networks but no dispatch is a fault, so it does not count.
    /// </summary>
    /// <remarks>
    /// The gateway dispatches a chain by its source zone's ingress interfaces, except a Gateway-sourced chain
    /// (<c>LOCAL_&lt;DST&gt;</c>), which it dispatches by the destination zone's egress interfaces.
    /// </remarks>
    private static bool DispatchZoneIsEmpty(string chain, ZoneTokens tokens, IReadOnlyList<UniFiNetworkConfig>? networks, IptablesDump v4)
    {
        var parts = chain.Split('_', 2);
        if (parts.Length != 2) return false;
        var token = parts[0] == "LOCAL" ? parts[1] : parts[0];
        if (token == "LOCAL" || v4.HasDispatchTo(token) || tokens.ZoneOf(token) is not { } zone) return false;
        return zone.NetworkIds.Count == 0
               && !(networks ?? []).Any(n => string.Equals(n.FirewallZoneId, zone.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static Func<string, string> DisplayZonePair(ZoneTokens tokens) => chain =>
    {
        var parts = chain.Split('_', 2);
        return parts.Length == 2 ? $"{tokens.NameOf(parts[0])} to {tokens.NameOf(parts[1])}" : chain;
    };

    // === Threat Intelligence: gateway agent blocking ===

    private static void AddBlocklistIssues(List<AuditIssue> issues, GatewayBlocklistExpectation expected, IptablesDump v4, IptablesDump v6,
        IpsetHeaders sets, string? gatewayName)
    {
        var hooks = new List<(string Where, int Count)>();
        foreach (var (family, dump) in new[] { ("IPv4", v4), ("IPv6", v6) })
            foreach (var builtin in new[] { "INPUT", "FORWARD" })
                hooks.Add(($"{family} {builtin}", dump.CountRules($"-A {builtin} -j {expected.ChainName}")));

        var missing = hooks.Where(h => h.Count == 0).Select(h => h.Where).ToList();
        if (missing.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayBlocklistNotHooked,
                Severity = AuditSeverity.Critical,
                DeviceName = gatewayName,
                Message = $"Automatic Blocking is not in effect on the gateway for {Join(missing)}: its rules are not in the path of that traffic, so blocked addresses get through.",
                RecommendedAction = RestartAgentAction,
                RuleId = "GW-TI-001",
                ScoreImpact = BlocklistHookMissingScoreImpact,
                Metadata = new() { ["missing_hooks"] = missing, [ActionKey] = UpgradeAgentAction },
            });
        }

        var doubled = hooks.Where(h => h.Count > 1).Select(h => h.Where).ToList();
        if (doubled.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayBlocklistHookDuplicated,
                Severity = AuditSeverity.Informational,
                DeviceName = gatewayName,
                Message = $"Automatic Blocking's rules are hooked in more than once on the gateway ({Join(doubled)}). Blocking still works; each extra hook checks the same traffic again.",
                RecommendedAction = "Upgrade the On-Site Agent on the gateway from Settings - Multi-Site. The current version removes the extra hooks.",
                RuleId = "GW-TI-002",
                ScoreImpact = 0,
                Metadata = new() { ["duplicate_hooks"] = doubled, [ActionKey] = UpgradeAgentAction },
            });
        }

        var drifted = new List<string>();
        if (!CoreMatches(v4.RulesOf(expected.ChainName), expected.CoreRulesV4)) drifted.Add("IPv4");
        if (!CoreMatches(v6.RulesOf(expected.ChainName), expected.CoreRulesV6)) drifted.Add("IPv6");
        if (drifted.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayBlocklistChainDrift,
                Severity = AuditSeverity.Recommended,
                DeviceName = gatewayName,
                Message = $"Automatic Blocking's rules on the gateway ({Join(drifted)}) differ from what Network Optimizer installs, so some blocking may not apply.",
                RecommendedAction = RestartAgentAction,
                RuleId = "GW-TI-003",
                ScoreImpact = BlocklistChainScoreImpact,
                Metadata = new() { ["families"] = drifted, [ActionKey] = UpgradeAgentAction },
            });
        }

        var empty = expected.SentEntries
            .Where(kv => kv.Value > 0 && sets.EntriesOf(kv.Key) is null or 0)
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(SetDisplayName)
            .ToList();
        if (empty.Count > 0)
        {
            issues.Add(new AuditIssue
            {
                Type = IssueTypes.GatewayBlocklistSetEmpty,
                Severity = AuditSeverity.Recommended,
                DeviceName = gatewayName,
                Message = $"The gateway does not hold addresses Network Optimizer sent it to block: {Join(empty)} {(empty.Count == 1 ? "is" : "are")} empty on the gateway.",
                RecommendedAction = RestartAgentAction,
                RuleId = "GW-TI-004",
                ScoreImpact = BlocklistSetScoreImpact,
                Metadata = new() { ["sets"] = empty, [ActionKey] = UpgradeAgentAction },
            });
        }
    }

    /// <summary>
    /// The chain matches when its rules, less the site's exception RETURNs, are the core rules in order. The one
    /// RETURN in the core (the server exemption) is kept.
    /// </summary>
    internal static bool CoreMatches(IReadOnlyList<string> actual, IReadOnlyList<string> core)
    {
        var coreSet = core.ToHashSet(StringComparer.Ordinal);
        var kept = actual.Where(r => coreSet.Contains(r) || !r.EndsWith(" -j RETURN", StringComparison.Ordinal)).ToList();
        return kept.SequenceEqual(core, StringComparer.Ordinal);
    }

    // === Zone tokens ===

    /// <summary>
    /// Maps each controller zone to its gateway token. The six built-in zones are fixed; a custom zone's
    /// CUSTOMn token is learned from where its policies landed, or failing that from its networks' interfaces.
    /// </summary>
    internal static ZoneTokens ResolveZoneTokens(IReadOnlyList<UniFiFirewallZone> zones, IReadOnlyList<FirewallRule>? policies,
        UdapiFilter? udapi, IReadOnlyList<UniFiNetworkConfig>? networks, IptablesDump v4)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in zones)
            if (FixedZoneTokens.TryGetValue(zone.ZoneKey ?? "", out var token))
                known[zone.Id] = token;

        var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        void Vote(string zoneId, string token)
        {
            if (!votes.TryGetValue(zoneId, out var tally)) votes[zoneId] = tally = new(StringComparer.Ordinal);
            tally[token] = tally.GetValueOrDefault(token) + 1;
        }

        foreach (var policy in policies ?? [])
        {
            if (udapi == null || policy.SourceZoneId is not { } src || policy.DestinationZoneId is not { } dst) continue;
            foreach (var chain in udapi.ChainsOf(policy.Id))
            {
                var parts = chain.Split('_', 2);
                if (parts.Length != 2) continue;
                if (!known.ContainsKey(src) && known.TryGetValue(dst, out var dstToken) && parts[1] == dstToken) Vote(src, parts[0]);
                if (!known.ContainsKey(dst) && known.TryGetValue(src, out var srcToken) && parts[0] == srcToken) Vote(dst, parts[1]);
            }
        }

        var learned = new Dictionary<string, string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (var (zoneId, tally) in votes)
        {
            var top = tally.OrderByDescending(kv => kv.Value).ToList();
            if (top.Count == 1 || top[0].Value > top[1].Value) learned[zoneId] = top[0].Key;
        }

        var guessed = new Dictionary<string, string>(learned, StringComparer.OrdinalIgnoreCase);
        foreach (var zone in zones.Where(z => !guessed.ContainsKey(z.Id)))
        {
            var found = (networks ?? [])
                .Where(n => IsLan(n) && (string.Equals(n.FirewallZoneId, zone.Id, StringComparison.OrdinalIgnoreCase)
                                         || zone.NetworkIds.Contains(n.Id, StringComparer.OrdinalIgnoreCase)))
                .SelectMany(n => v4.ForwardZones(BridgeOf(n)))
                .Distinct()
                .ToList();
            if (found.Count == 1) guessed[zone.Id] = found[0];
        }

        return new ZoneTokens(learned, guessed, zones);
    }

    /// <summary>Each zone's gateway token, and back to its UniFi Network name.</summary>
    internal sealed class ZoneTokens(Dictionary<string, string> learned, Dictionary<string, string> guessed, IReadOnlyList<UniFiFirewallZone> zones)
    {
        /// <summary>The zone's token; <paramref name="includeInterfaceGuesses"/> false leaves out tokens read off its networks' interfaces.</summary>
        public string? Of(string? zoneId, bool includeInterfaceGuesses = true) =>
            zoneId == null ? null
            : (includeInterfaceGuesses ? guessed : learned).TryGetValue(zoneId, out var token) ? token : null;

        public string NameOf(string token) => ZoneOf(token)?.Name ?? token;

        /// <summary>The UniFi Network zone behind a gateway token; null when the token is not resolved.</summary>
        public UniFiFirewallZone? ZoneOf(string token)
        {
            var id = guessed.FirstOrDefault(kv => kv.Value == token).Key;
            return zones.FirstOrDefault(z => z.Id == id);
        }
    }

    // === Helpers ===

    /// <summary>A gateway agent set by what it holds: <c>netopt_feed_v4</c> is "the public block lists (IPv4)".</summary>
    internal static string SetDisplayName(string set)
    {
        var family = set.EndsWith("_v6", StringComparison.Ordinal) ? "IPv6" : "IPv4";
        var what = set switch
        {
            _ when set.StartsWith("netopt_feed_", StringComparison.Ordinal) => "the public block lists",
            _ when set.StartsWith("netopt_out_", StringComparison.Ordinal) => "the outbound block list",
            _ when set.StartsWith("netopt_block_", StringComparison.Ordinal) => "your blocked addresses",
            _ => set,
        };
        return $"{what} ({family})";
    }

    private static bool IsWan(UniFiNetworkConfig n) => string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase);

    private static bool IsLan(UniFiNetworkConfig n) =>
        string.Equals(n.Purpose, "corporate", StringComparison.OrdinalIgnoreCase)
        || string.Equals(n.Purpose, "guest", StringComparison.OrdinalIgnoreCase);

    /// <summary>The gateway bridge of a LAN network: <c>br&lt;VLAN&gt;</c>, and <c>br0</c> for the untagged network.</summary>
    internal static string BridgeOf(UniFiNetworkConfig n) => $"br{(n.VlanEnabled && n.Vlan is > 0 ? n.Vlan : 0)}";

    private static string Join(IReadOnlyList<string> names)
    {
        var shown = names.Take(MaxListed).ToList();
        var more = names.Count - shown.Count;
        var text = shown.Count switch
        {
            1 => shown[0],
            2 => $"{shown[0]} and {shown[1]}",
            _ => string.Join(", ", shown.Take(shown.Count - 1)) + ", and " + shown[^1],
        };
        return more > 0 ? $"{string.Join(", ", shown)}, and {more} more" : text;
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    // === Parsers ===

    /// <summary>An <c>iptables-save</c> filter table: chain names and rules, quotes removed.</summary>
    internal sealed partial class IptablesDump
    {
        private readonly List<string> _rules = [];
        private readonly HashSet<string> _chains = new(StringComparer.Ordinal);

        public bool HasRules => _rules.Count > 0;
        public int RuleCount => _rules.Count;

        public static IptablesDump Parse(string? text)
        {
            var dump = new IptablesDump();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith(':')) dump._chains.Add(line[1..].Split(' ')[0]);
                else if (line.StartsWith("-A ", StringComparison.Ordinal)) dump._rules.Add(Collapse(line.Replace("\"", "")));
            }
            return dump;
        }

        private static string Collapse(string line) => SpacesRegex().Replace(line, " ");

        public int CountRules(string rule) => _rules.Count(r => r == rule);

        public IReadOnlyList<string> RulesOf(string chain) =>
            _rules.Where(r => r.StartsWith($"-A {chain} ", StringComparison.Ordinal)).ToList();

        /// <summary>Whether the chain exists and some rule jumps to it.</summary>
        public bool IsReachable(string chain) =>
            _chains.Contains(chain) && _rules.Any(r => r.EndsWith($" -j {chain}", StringComparison.Ordinal));

        /// <summary>The zone tokens forwarded traffic from <paramref name="iface"/> is dispatched to.</summary>
        public HashSet<string> ForwardZones(string iface) => Dispatch(ForwardDispatchRegex(), iface);

        /// <summary>The zone tokens traffic from <paramref name="iface"/> to the gateway itself is dispatched to.</summary>
        public HashSet<string> InputZones(string iface) => Dispatch(InputDispatchRegex(), iface);

        /// <summary>Whether any interface's forwarded, gateway-bound, or gateway-sent traffic is dispatched by the zone token.</summary>
        public bool HasDispatchTo(string token) =>
            _rules.Any(r => new[] { ForwardDispatchRegex(), InputDispatchRegex(), OutputDispatchRegex() }
                .Any(p => p.Match(r) is { Success: true } m && m.Groups["zone"].Value == token));

        private HashSet<string> Dispatch(Regex pattern, string iface)
        {
            var zones = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in _rules)
            {
                var m = pattern.Match(rule);
                if (m.Success && InterfaceMatches(m.Groups["if"].Value, iface)) zones.Add(m.Groups["zone"].Value);
            }
            return zones;
        }

        // iptables "+" is a prefix wildcard (wgclt+).
        private static bool InterfaceMatches(string pattern, string iface) =>
            pattern.EndsWith('+') ? iface.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == iface;

        [GeneratedRegex(@"\s+")]
        private static partial Regex SpacesRegex();

        // WAN_PF_IN (port forwards) sits beside WAN_IN for the same interface; it is not a zone.
        [GeneratedRegex(@"^-A UBIOS_FORWARD_IN_USER -i (?<if>\S+) .*-j UBIOS_(?<zone>(?!WAN_PF)[A-Z0-9]+)_IN_USER$")]
        private static partial Regex ForwardDispatchRegex();

        [GeneratedRegex(@"^-A UBIOS_INPUT_USER_HOOK -i (?<if>\S+) .*-j UBIOS_(?<zone>[A-Z0-9]+)_LOCAL_USER$")]
        private static partial Regex InputDispatchRegex();

        [GeneratedRegex(@"^-A UBIOS_OUTPUT_USER_HOOK -o (?<if>\S+) .*-j UBIOS_LOCAL_(?<zone>[A-Z0-9]+)_USER$")]
        private static partial Regex OutputDispatchRegex();
    }

    /// <summary>
    /// The live udapi firewall filter: which chains hold each policy's rules. UniFi tags only some compiled rules
    /// with their policy ID (every logged policy, not every unlogged one), so untagged rules are also kept by
    /// description, which is the policy's name.
    /// </summary>
    internal sealed class UdapiFilter
    {
        private readonly Dictionary<string, HashSet<string>> _chainsByPolicy = new(StringComparer.OrdinalIgnoreCase);
        // Untagged rules per chain, counted by description: policies often share a name (Net. Opt. Exception).
        private readonly Dictionary<string, Dictionary<string, int>> _untaggedByChain = new(StringComparer.Ordinal);

        /// <summary>
        /// Claims one untagged rule in <paramref name="chain"/> described as <paramref name="policyName"/>. Each rule
        /// vouches for one policy, so a present policy never covers for a missing one of the same name.
        /// </summary>
        public bool TakeUntaggedRuleNamed(string chain, string? policyName)
        {
            if (string.IsNullOrWhiteSpace(policyName) || !_untaggedByChain.TryGetValue(chain, out var names)
                || !names.TryGetValue(policyName.Trim(), out var left) || left == 0) return false;
            names[policyName.Trim()] = left - 1;
            return true;
        }

        /// <summary>Every chain holding an untagged rule described as <paramref name="policyName"/>.</summary>
        public IEnumerable<string> ChainsWithUntaggedRuleNamed(string? policyName) =>
            string.IsNullOrWhiteSpace(policyName) ? []
            : _untaggedByChain.Where(kv => kv.Value.ContainsKey(policyName.Trim())).Select(kv => kv.Key).OrderBy(c => c, StringComparer.Ordinal);

        /// <summary>Chains that hold at least one user policy.</summary>
        public HashSet<string> PolicyChains { get; } = new(StringComparer.Ordinal);

        public IReadOnlySet<string> ChainsOf(string policyId) =>
            _chainsByPolicy.TryGetValue(policyId, out var chains) ? chains : new HashSet<string>();

        /// <summary>Null when the text is missing or not the expected JSON shape.</summary>
        public static UdapiFilter? Parse(string? json, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                var filter = new UdapiFilter();
                foreach (var chain in doc.RootElement.EnumerateArray())
                {
                    if (!chain.TryGetProperty("config", out var config) || !config.TryGetProperty("name", out var nameEl)
                        || nameEl.GetString() is not { Length: > 0 } name) continue;
                    if (!chain.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) continue;
                    foreach (var rule in rules.EnumerateArray())
                    {
                        if (!rule.TryGetProperty("triggerTag", out var tag) || tag.ValueKind != JsonValueKind.Object)
                        {
                            if (rule.TryGetProperty("description", out var descEl) && descEl.GetString()?.Trim() is { Length: > 0 } desc)
                            {
                                if (!filter._untaggedByChain.TryGetValue(name, out var names))
                                    filter._untaggedByChain[name] = names = new(StringComparer.Ordinal);
                                names[desc] = names.GetValueOrDefault(desc) + 1;
                            }
                            continue;
                        }
                        if (!tag.TryGetProperty("type", out var type) || type.GetString() != "FIREWALL_POLICY") continue;
                        if (!tag.TryGetProperty("id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) continue;
                        if (!filter._chainsByPolicy.TryGetValue(id, out var set)) filter._chainsByPolicy[id] = set = new(StringComparer.Ordinal);
                        set.Add(name);
                        filter.PolicyChains.Add(name);
                    }
                }
                return filter;
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Gateway firewall: the udapi filter could not be read ({Error}); policy checks skipped", ex.Message);
                return null;
            }
        }
    }

    /// <summary><c>ipset list -t</c> headers: each set's entry count.</summary>
    internal sealed class IpsetHeaders
    {
        private readonly Dictionary<string, int> _entries = new(StringComparer.Ordinal);

        public int? EntriesOf(string set) => _entries.TryGetValue(set, out var n) ? n : null;

        public static IpsetHeaders Parse(string? text)
        {
            var headers = new IpsetHeaders();
            string? current = null;
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("Name:", StringComparison.Ordinal)) current = line[5..].Trim();
                else if (current != null && line.StartsWith("Number of entries:", StringComparison.Ordinal)
                         && int.TryParse(line[18..].Trim(), out var n))
                    headers._entries[current] = n;
            }
            return headers;
        }
    }
}
