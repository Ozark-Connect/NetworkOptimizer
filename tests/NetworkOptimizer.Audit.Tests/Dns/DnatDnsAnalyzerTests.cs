using System.Text.Json;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

/// <summary>
/// Unit tests for DnatDnsAnalyzer
/// </summary>
public class DnatDnsAnalyzerTests
{
    private readonly DnatDnsAnalyzer _analyzer = new();

    #region Helper Methods

    private static List<NetworkInfo> CreateTestNetworks(params (string id, string name, string subnet, bool dhcpEnabled)[] networks)
    {
        return networks.Select(n => new NetworkInfo
        {
            Id = n.id,
            Name = n.name,
            VlanId = 1,
            Subnet = n.subnet,
            DhcpEnabled = n.dhcpEnabled
        }).ToList();
    }

    private static List<NetworkInfo> CreateTestNetworksWithVlans(params (string id, string name, string subnet, int vlanId)[] networks)
    {
        return networks.Select(n => new NetworkInfo
        {
            Id = n.id,
            Name = n.name,
            VlanId = n.vlanId,
            Subnet = n.subnet,
            DhcpEnabled = true
        }).ToList();
    }

    private static string CreateDnatRule(
        string id,
        string sourceFilterType,
        string? sourceAddress = null,
        string? networkConfId = null,
        string destPort = "53",
        string protocol = "udp",
        bool enabled = true,
        string redirectIp = "192.168.1.1",
        string? inInterface = null,
        string? description = null)
    {
        var sourceFilter = sourceFilterType == "NETWORK_CONF"
            ? $"\"filter_type\": \"NETWORK_CONF\", \"network_conf_id\": \"{networkConfId}\""
            : sourceFilterType == "ANY"
                ? "\"filter_type\": \"ANY\""
                : $"\"filter_type\": \"ADDRESS_AND_PORT\", \"address\": \"{sourceAddress}\"";

        var inInterfaceField = inInterface != null ? $"\"in_interface\": \"{inInterface}\"," : "";
        var desc = description ?? "Test DNAT";

        return $$"""
        {
            "_id": "{{id}}",
            "description": "{{desc}}",
            "type": "DNAT",
            "enabled": {{enabled.ToString().ToLower()}},
            "protocol": "{{protocol}}",
            "ip_version": "IPV4",
            "ip_address": "{{redirectIp}}",
            {{inInterfaceField}}
            "destination_filter": {
                "filter_type": "ADDRESS_AND_PORT",
                "port": "{{destPort}}"
            },
            "source_filter": {
                {{sourceFilter}}
            }
        }
        """;
    }

    private static JsonElement ParseNatRules(params string[] rules)
    {
        var json = $"[{string.Join(",", rules)}]";
        return JsonDocument.Parse(json).RootElement;
    }

    #endregion

    #region No NAT Rules Tests

    [Fact]
    public void Analyze_WithNullNatRules_ReturnsEmptyResult()
    {
        var networks = CreateTestNetworks(("net1", "LAN", "192.168.1.0/24", true));

        var result = _analyzer.Analyze(null, networks);

        Assert.False(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void Analyze_WithEmptyNetworks_ReturnsEmptyResult()
    {
        var natRules = ParseNatRules(CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24"));

        var result = _analyzer.Analyze(natRules, null);

        Assert.False(result.HasDnatDnsRules);
    }

    [Fact]
    public void Analyze_WithNonDhcpNetwork_StillChecksCoverage()
    {
        // Non-DHCP networks still need DNAT coverage (static IP devices can make DNS queries)
        var networks = CreateTestNetworks(("net1", "LAN", "192.168.1.0/24", false));
        var natRules = ParseNatRules();

        var result = _analyzer.Analyze(natRules, networks);

        Assert.False(result.HasFullCoverage); // Non-DHCP network still needs coverage
        Assert.Single(result.UncoveredNetworkIds);
        Assert.Contains("net1", result.UncoveredNetworkIds);
    }

    [Fact]
    public void Analyze_WithEmptyNatRulesArray_ReturnsNoCoverage()
    {
        var networks = CreateTestNetworks(("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules();

        var result = _analyzer.Analyze(natRules, networks);

        Assert.False(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage);
        Assert.Single(result.UncoveredNetworkIds);
    }

    #endregion

    #region Network Reference Coverage Tests

    [Fact]
    public void Analyze_WithNetworkRefDnat_CoversSpecificNetwork()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage); // Only net1 covered
        Assert.Single(result.CoveredNetworkIds);
        Assert.Contains("net1", result.CoveredNetworkIds);
        Assert.Single(result.UncoveredNetworkIds);
        Assert.Contains("net2", result.UncoveredNetworkIds);
    }

    [Fact]
    public void Analyze_WithMultipleNetworkRefDnat_CoversAllNetworks()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"),
            CreateDnatRule("2", "NETWORK_CONF", networkConfId: "net2"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
        Assert.Equal(2, result.CoveredNetworkIds.Count);
        Assert.Empty(result.UncoveredNetworkIds);
    }

    #endregion

    #region Subnet Coverage Tests

    [Fact]
    public void Analyze_WithSubnetDnat_CoversMatchingNetwork()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
        Assert.Single(result.CoveredNetworkIds);
        Assert.Contains("net1", result.CoveredNetworkIds);
    }

    [Fact]
    public void Analyze_WithLargerSubnetDnat_CoversMultipleNetworks()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true));
        // /16 covers both /24 networks
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.0.0/16"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
        Assert.Equal(2, result.CoveredNetworkIds.Count);
    }

    [Fact]
    public void Analyze_WithSmallerSubnetDnat_DoesNotCoverLargerNetwork()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.0.0/16", true)); // Larger network
        // /24 is smaller than /16, doesn't cover the whole network
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage);
        Assert.Empty(result.CoveredNetworkIds);
    }

    [Fact]
    public void Analyze_WithNonMatchingSubnet_DoesNotCover()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "10.0.0.0/24")); // Different subnet

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage);
        Assert.Single(result.UncoveredNetworkIds);
    }

    #endregion

    #region Single IP Tests (Abnormal Configuration)

    [Fact]
    public void Analyze_WithSingleIpDnat_FlagsAsAbnormal()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.100")); // Single IP

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage); // Single IP doesn't provide full coverage
        Assert.Single(result.SingleIpRules);
        Assert.Contains("192.168.1.100", result.SingleIpRules);
    }

    [Fact]
    public void Analyze_WithMultipleSingleIpDnat_FlagsAllAsAbnormal()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.100"),
            CreateDnatRule("2", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.101"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.Equal(2, result.SingleIpRules.Count);
    }

    #endregion

    #region Protocol Filter Tests

    [Fact]
    public void Analyze_WithTcpOnlyDnat_IgnoresRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", protocol: "tcp"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.False(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage);
    }

    [Fact]
    public void Analyze_WithTcpUdpDnat_IncludesRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", protocol: "tcp_udp"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
    }

    [Fact]
    public void Analyze_WithAllProtocolDnat_IncludesRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", protocol: "all"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
    }

    #endregion

    #region Disabled Rule Tests

    [Fact]
    public void Analyze_WithDisabledDnat_IgnoresRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", enabled: false));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.False(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage);
    }

    #endregion

    #region Non-Port-53 Tests

    [Fact]
    public void Analyze_WithNonPort53Dnat_IgnoresRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", destPort: "80"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.False(result.HasDnatDnsRules);
    }

    [Fact]
    public void Analyze_WithPort53InRange_IncludesRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", destPort: "1:100")); // Range includes 53

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
    }

    [Fact]
    public void Analyze_WithPort53InList_IncludesRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", destPort: "22,53,80"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
    }

    #endregion

    #region Non-DNAT Rule Tests

    [Fact]
    public void Analyze_WithSnatRule_IgnoresRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        // SNAT rule instead of DNAT
        var snatRule = """
        {
            "_id": "1",
            "type": "SNAT",
            "enabled": true,
            "protocol": "udp",
            "ip_address": "192.168.1.1",
            "destination_filter": { "filter_type": "ADDRESS_AND_PORT", "port": "53" },
            "source_filter": { "filter_type": "ADDRESS_AND_PORT", "address": "192.168.1.0/24" }
        }
        """;
        var natRules = JsonDocument.Parse($"[{snatRule}]").RootElement;

        var result = _analyzer.Analyze(natRules, networks);

        Assert.False(result.HasDnatDnsRules);
    }

    #endregion

    #region Redirect Target Tests

    [Fact]
    public void Analyze_SetsRedirectTargetFromFirstRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24", redirectIp: "10.0.0.1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.Equal("10.0.0.1", result.RedirectTargetIp);
    }

    #endregion

    #region Mixed Coverage Tests

    [Fact]
    public void Analyze_WithMixedCoverageTypes_CumulativesCoverage()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true),
            ("net3", "Guest", "192.168.3.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"), // Network ref
            CreateDnatRule("2", "ADDRESS_AND_PORT", sourceAddress: "192.168.2.0/24"), // Subnet
            CreateDnatRule("3", "ADDRESS_AND_PORT", sourceAddress: "192.168.3.100")); // Single IP

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.False(result.HasFullCoverage); // Single IP doesn't cover net3
        Assert.Equal(2, result.CoveredNetworkIds.Count); // net1 and net2
        Assert.Single(result.UncoveredNetworkIds); // net3
        Assert.Single(result.SingleIpRules); // One single IP rule
    }

    #endregion

    #region CidrCoversSubnet Tests

    [Fact]
    public void CidrCoversSubnet_ExactMatch_ReturnsTrue()
    {
        Assert.True(DnatDnsAnalyzer.CidrCoversSubnet("192.168.1.0/24", "192.168.1.0/24"));
    }

    [Fact]
    public void CidrCoversSubnet_LargerCidrCoversSmaller_ReturnsTrue()
    {
        Assert.True(DnatDnsAnalyzer.CidrCoversSubnet("192.168.0.0/16", "192.168.1.0/24"));
    }

    [Fact]
    public void CidrCoversSubnet_SmallerCidrDoesNotCoverLarger_ReturnsFalse()
    {
        Assert.False(DnatDnsAnalyzer.CidrCoversSubnet("192.168.1.0/24", "192.168.0.0/16"));
    }

    [Fact]
    public void CidrCoversSubnet_DifferentNetwork_ReturnsFalse()
    {
        Assert.False(DnatDnsAnalyzer.CidrCoversSubnet("192.168.1.0/24", "192.168.2.0/24"));
    }

    [Fact]
    public void CidrCoversSubnet_ClassA_ReturnsTrue()
    {
        Assert.True(DnatDnsAnalyzer.CidrCoversSubnet("10.0.0.0/8", "10.1.2.0/24"));
    }

    [Fact]
    public void CidrCoversSubnet_InvalidCidr_ReturnsFalse()
    {
        Assert.False(DnatDnsAnalyzer.CidrCoversSubnet("invalid", "192.168.1.0/24"));
        Assert.False(DnatDnsAnalyzer.CidrCoversSubnet("192.168.1.0/24", "invalid"));
    }

    #endregion

    #region Interface Coverage Tests (in_interface with source ANY)

    [Fact]
    public void Analyze_WithInInterface_SourceAny_CoversInterfaceNetwork()
    {
        // When in_interface is set and source is ANY, the rule covers that network
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ANY", inInterface: "net1", redirectIp: "192.168.1.1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.Single(result.CoveredNetworkIds);
        Assert.Contains("net1", result.CoveredNetworkIds);
        Assert.Single(result.UncoveredNetworkIds);
        Assert.Contains("net2", result.UncoveredNetworkIds);
    }

    [Fact]
    public void Analyze_WithInInterface_AndNetworkRef_BothWork()
    {
        // in_interface can be combined with explicit network reference
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1", inInterface: "net1", redirectIp: "192.168.1.1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
        Assert.Single(result.Rules);
        Assert.Equal("net1", result.Rules[0].InInterface);
    }

    [Fact]
    public void Analyze_ExtractsInInterfaceFromRule()
    {
        var networks = CreateTestNetworks(("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1", inInterface: "interface-123"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.Single(result.Rules);
        Assert.Equal("interface-123", result.Rules[0].InInterface);
    }

    [Fact]
    public void Analyze_WithMultipleInterfaceRules_CoversMultipleNetworks()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true),
            ("net3", "Guest", "192.168.3.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ANY", inInterface: "net1", redirectIp: "192.168.1.1"),
            CreateDnatRule("2", "ANY", inInterface: "net2", redirectIp: "192.168.2.1"),
            CreateDnatRule("3", "ANY", inInterface: "net3", redirectIp: "192.168.3.1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.True(result.HasDnatDnsRules);
        Assert.True(result.HasFullCoverage);
        Assert.Equal(3, result.CoveredNetworkIds.Count);
        Assert.Empty(result.UncoveredNetworkIds);
    }

    [Fact]
    public void Analyze_InterfaceCoverageType_SetCorrectly()
    {
        var networks = CreateTestNetworks(("net1", "LAN", "192.168.1.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ANY", inInterface: "net1", redirectIp: "192.168.1.1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.Single(result.Rules);
        Assert.Equal("interface", result.Rules[0].CoverageType);
        Assert.Equal("net1", result.Rules[0].NetworkId);
    }

    #endregion

    #region Multiple Redirect Target Tests

    [Fact]
    public void Analyze_TracksRedirectIpPerRule()
    {
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1", redirectIp: "192.168.1.1"),
            CreateDnatRule("2", "NETWORK_CONF", networkConfId: "net2", redirectIp: "192.168.2.1"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.Equal(2, result.Rules.Count);
        Assert.Equal("192.168.1.1", result.Rules[0].RedirectIp);
        Assert.Equal("192.168.2.1", result.Rules[1].RedirectIp);
    }

    [Fact]
    public void Analyze_RedirectTargetIp_UsesFirstRule()
    {
        // RedirectTargetIp should be from the first rule for backward compatibility
        var networks = CreateTestNetworks(
            ("net1", "LAN", "192.168.1.0/24", true),
            ("net2", "IoT", "192.168.2.0/24", true));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1", redirectIp: "10.0.0.1"),
            CreateDnatRule("2", "NETWORK_CONF", networkConfId: "net2", redirectIp: "10.0.0.2"));

        var result = _analyzer.Analyze(natRules, networks);

        Assert.Equal("10.0.0.1", result.RedirectTargetIp);
    }

    #endregion

    #region Excluded VLAN Tests

    [Fact]
    public void Analyze_WithExcludedVlans_ExcludesNetworksFromCoverageCheck()
    {
        // 3 networks: VLAN 10, 20, 100. Only VLAN 10 has DNAT coverage. VLAN 100 is excluded.
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "IoT", "192.168.2.0/24", 20),
            ("net3", "Management", "192.168.100.0/24", 100));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"));
        var excludedVlans = new List<int> { 100 };

        var result = _analyzer.Analyze(natRules, networks, excludedVlans);

        // With VLAN 100 excluded, only 2 networks are considered
        // net1 is covered, net2 is uncovered, net3 (VLAN 100) is excluded
        Assert.Single(result.CoveredNetworkNames);
        Assert.Contains("LAN", result.CoveredNetworkNames);
        Assert.Single(result.UncoveredNetworkNames);
        Assert.Contains("IoT", result.UncoveredNetworkNames);
        Assert.Single(result.ExcludedNetworkNames);
        Assert.Contains("Management", result.ExcludedNetworkNames);
    }

    [Fact]
    public void Analyze_WithExcludedVlans_NullExclusions_IncludesAllNetworks()
    {
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "Management", "192.168.100.0/24", 100));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"));

        var result = _analyzer.Analyze(natRules, networks, excludedVlanIds: null);

        // No exclusions - both networks considered
        Assert.Single(result.CoveredNetworkNames);
        Assert.Single(result.UncoveredNetworkNames);
        Assert.Empty(result.ExcludedNetworkNames);
    }

    [Fact]
    public void Analyze_WithExcludedVlans_EmptyExclusions_IncludesAllNetworks()
    {
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "Management", "192.168.100.0/24", 100));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"));
        var excludedVlans = new List<int>();

        var result = _analyzer.Analyze(natRules, networks, excludedVlans);

        // Empty exclusions - both networks considered
        Assert.Single(result.CoveredNetworkNames);
        Assert.Single(result.UncoveredNetworkNames);
        Assert.Empty(result.ExcludedNetworkNames);
    }

    [Fact]
    public void Analyze_WithExcludedVlans_AllNetworksExcluded_ReturnsFullCoverage()
    {
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "Management", "192.168.100.0/24", 100));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "ADDRESS_AND_PORT", sourceAddress: "192.168.1.0/24"));
        var excludedVlans = new List<int> { 10, 100 };

        var result = _analyzer.Analyze(natRules, networks, excludedVlans);

        // All networks excluded - no networks to check, so "full coverage" by default
        Assert.Empty(result.CoveredNetworkNames);
        Assert.Empty(result.UncoveredNetworkNames);
        Assert.Equal(2, result.ExcludedNetworkNames.Count);
        Assert.True(result.HasFullCoverage);
    }

    [Fact]
    public void Analyze_WithExcludedVlans_ExcludingUncoveredNetwork_AchievesFullCoverage()
    {
        // 2 networks: net1 (VLAN 10) has coverage, net2 (VLAN 100) does not
        // By excluding VLAN 100, we achieve full coverage
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "Management", "192.168.100.0/24", 100));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"));
        var excludedVlans = new List<int> { 100 };

        var result = _analyzer.Analyze(natRules, networks, excludedVlans);

        Assert.True(result.HasFullCoverage);
        Assert.Single(result.CoveredNetworkNames);
        Assert.Empty(result.UncoveredNetworkNames);
        Assert.Single(result.ExcludedNetworkNames);
    }

    [Fact]
    public void Analyze_WithExcludedVlans_MultipleVlansExcluded()
    {
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "IoT", "192.168.2.0/24", 20),
            ("net3", "Guest", "192.168.3.0/24", 30),
            ("net4", "Management", "192.168.100.0/24", 100),
            ("net5", "Servers", "192.168.200.0/24", 200));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"),
            CreateDnatRule("2", "NETWORK_CONF", networkConfId: "net2"));
        var excludedVlans = new List<int> { 100, 200 };

        var result = _analyzer.Analyze(natRules, networks, excludedVlans);

        // net1 and net2 covered, net3 uncovered, net4 and net5 excluded
        Assert.Equal(2, result.CoveredNetworkNames.Count);
        Assert.Single(result.UncoveredNetworkNames);
        Assert.Contains("Guest", result.UncoveredNetworkNames);
        Assert.Equal(2, result.ExcludedNetworkNames.Count);
        Assert.Contains("Management", result.ExcludedNetworkNames);
        Assert.Contains("Servers", result.ExcludedNetworkNames);
    }

    [Fact]
    public void Analyze_WithExcludedVlans_NonMatchingVlanId_NoEffect()
    {
        var networks = CreateTestNetworksWithVlans(
            ("net1", "LAN", "192.168.1.0/24", 10),
            ("net2", "IoT", "192.168.2.0/24", 20));
        var natRules = ParseNatRules(
            CreateDnatRule("1", "NETWORK_CONF", networkConfId: "net1"));
        var excludedVlans = new List<int> { 999 }; // Non-existent VLAN

        var result = _analyzer.Analyze(natRules, networks, excludedVlans);

        // VLAN 999 doesn't exist, so no exclusions
        Assert.Single(result.CoveredNetworkNames);
        Assert.Single(result.UncoveredNetworkNames);
        Assert.Empty(result.ExcludedNetworkNames);
    }

    #endregion
}
