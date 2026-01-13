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

    private static string CreateDnatRule(
        string id,
        string sourceFilterType,
        string? sourceAddress = null,
        string? networkConfId = null,
        string destPort = "53",
        string protocol = "udp",
        bool enabled = true,
        string redirectIp = "192.168.1.1")
    {
        var sourceFilter = sourceFilterType == "NETWORK_CONF"
            ? $"\"filter_type\": \"NETWORK_CONF\", \"network_conf_id\": \"{networkConfId}\""
            : $"\"filter_type\": \"ADDRESS_AND_PORT\", \"address\": \"{sourceAddress}\"";

        return $$"""
        {
            "_id": "{{id}}",
            "description": "Test DNAT",
            "type": "DNAT",
            "enabled": {{enabled.ToString().ToLower()}},
            "protocol": "{{protocol}}",
            "ip_version": "IPV4",
            "ip_address": "{{redirectIp}}",
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
}
