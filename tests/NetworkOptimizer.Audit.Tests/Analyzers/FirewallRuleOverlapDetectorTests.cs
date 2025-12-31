using FluentAssertions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class FirewallRuleOverlapDetectorTests
{
    #region ProtocolsOverlap Tests

    [Theory]
    [InlineData("tcp", "tcp", true)]
    [InlineData("udp", "udp", true)]
    [InlineData("icmp", "icmp", true)]
    [InlineData("all", "all", true)]
    public void ProtocolsOverlap_SameProtocol_ReturnsTrue(string p1, string p2, bool expected)
    {
        var rule1 = CreateRule(protocol: p1);
        var rule2 = CreateRule(protocol: p2);

        FirewallRuleOverlapDetector.ProtocolsOverlap(rule1, rule2).Should().Be(expected);
    }

    [Theory]
    [InlineData("tcp", "udp", false)]
    [InlineData("tcp", "icmp", false)]
    [InlineData("udp", "icmp", false)]
    public void ProtocolsOverlap_DifferentProtocols_ReturnsFalse(string p1, string p2, bool expected)
    {
        var rule1 = CreateRule(protocol: p1);
        var rule2 = CreateRule(protocol: p2);

        FirewallRuleOverlapDetector.ProtocolsOverlap(rule1, rule2).Should().Be(expected);
    }

    [Theory]
    [InlineData("all", "tcp", true)]
    [InlineData("all", "udp", true)]
    [InlineData("all", "icmp", true)]
    [InlineData("tcp", "all", true)]
    [InlineData("udp", "all", true)]
    public void ProtocolsOverlap_AllMatchesEverything_ReturnsTrue(string p1, string p2, bool expected)
    {
        var rule1 = CreateRule(protocol: p1);
        var rule2 = CreateRule(protocol: p2);

        FirewallRuleOverlapDetector.ProtocolsOverlap(rule1, rule2).Should().Be(expected);
    }

    [Theory]
    [InlineData("tcp_udp", "tcp", true)]
    [InlineData("tcp_udp", "udp", true)]
    [InlineData("tcp", "tcp_udp", true)]
    [InlineData("udp", "tcp_udp", true)]
    [InlineData("tcp_udp", "tcp_udp", true)]
    public void ProtocolsOverlap_TcpUdpOverlapsWithTcpOrUdp_ReturnsTrue(string p1, string p2, bool expected)
    {
        var rule1 = CreateRule(protocol: p1);
        var rule2 = CreateRule(protocol: p2);

        FirewallRuleOverlapDetector.ProtocolsOverlap(rule1, rule2).Should().Be(expected);
    }

    [Fact]
    public void ProtocolsOverlap_TcpUdpDoesNotOverlapWithIcmp_ReturnsFalse()
    {
        var rule1 = CreateRule(protocol: "tcp_udp");
        var rule2 = CreateRule(protocol: "icmp");

        FirewallRuleOverlapDetector.ProtocolsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void ProtocolsOverlap_NullProtocolTreatedAsAll_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: null);
        var rule2 = CreateRule(protocol: "tcp");

        FirewallRuleOverlapDetector.ProtocolsOverlap(rule1, rule2).Should().BeTrue();
    }

    #endregion

    #region SourcesOverlap Tests

    [Fact]
    public void SourcesOverlap_BothAny_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "ANY");
        var rule2 = CreateRule(sourceMatchingTarget: "ANY");

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_OneIsAny_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "ANY");
        var rule2 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net1" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_NullMatchingTargetTreatedAsAny_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceMatchingTarget: null);
        var rule2 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net1" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_DifferentTargetTypes_ReturnsFalse()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net1" });
        var rule2 = CreateRule(sourceMatchingTarget: "IP", sourceIps: new List<string> { "192.168.1.1" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void SourcesOverlap_SameNetworkIds_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net1", "net2" });
        var rule2 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net2", "net3" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_DifferentNetworkIds_ReturnsFalse()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net1", "net2" });
        var rule2 = CreateRule(sourceMatchingTarget: "NETWORK", sourceNetworkIds: new List<string> { "net3", "net4" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void SourcesOverlap_SameIps_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "IP", sourceIps: new List<string> { "192.168.1.1", "192.168.1.2" });
        var rule2 = CreateRule(sourceMatchingTarget: "IP", sourceIps: new List<string> { "192.168.1.2", "192.168.1.3" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_DifferentIps_ReturnsFalse()
    {
        var rule1 = CreateRule(sourceMatchingTarget: "IP", sourceIps: new List<string> { "192.168.1.1" });
        var rule2 = CreateRule(sourceMatchingTarget: "IP", sourceIps: new List<string> { "192.168.1.2" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeFalse();
    }

    #endregion

    #region DestinationsOverlap Tests

    [Fact]
    public void DestinationsOverlap_BothAny_ReturnsTrue()
    {
        var rule1 = CreateRule(destMatchingTarget: "ANY");
        var rule2 = CreateRule(destMatchingTarget: "ANY");

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void DestinationsOverlap_OneIsAny_ReturnsTrue()
    {
        var rule1 = CreateRule(destMatchingTarget: "ANY");
        var rule2 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "example.com" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void DestinationsOverlap_DifferentTargetTypes_ReturnsFalse()
    {
        var rule1 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "example.com" });
        var rule2 = CreateRule(destMatchingTarget: "NETWORK", destNetworkIds: new List<string> { "net1" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void DestinationsOverlap_WebVsIp_ReturnsFalse()
    {
        var rule1 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "example.com" });
        var rule2 = CreateRule(destMatchingTarget: "IP", destIps: new List<string> { "192.168.1.1" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void DestinationsOverlap_SameNetworkIds_ReturnsTrue()
    {
        var rule1 = CreateRule(destMatchingTarget: "NETWORK", destNetworkIds: new List<string> { "net1", "net2" });
        var rule2 = CreateRule(destMatchingTarget: "NETWORK", destNetworkIds: new List<string> { "net2", "net3" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void DestinationsOverlap_DifferentNetworkIds_ReturnsFalse()
    {
        var rule1 = CreateRule(destMatchingTarget: "NETWORK", destNetworkIds: new List<string> { "net1" });
        var rule2 = CreateRule(destMatchingTarget: "NETWORK", destNetworkIds: new List<string> { "net2" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void DestinationsOverlap_SameIps_ReturnsTrue()
    {
        var rule1 = CreateRule(destMatchingTarget: "IP", destIps: new List<string> { "10.0.0.1" });
        var rule2 = CreateRule(destMatchingTarget: "IP", destIps: new List<string> { "10.0.0.1" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void DestinationsOverlap_SameWebDomains_ReturnsTrue()
    {
        var rule1 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "example.com", "test.com" });
        var rule2 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "test.com", "other.com" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void DestinationsOverlap_DifferentWebDomains_ReturnsFalse()
    {
        var rule1 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "example.com" });
        var rule2 = CreateRule(destMatchingTarget: "WEB", webDomains: new List<string> { "other.com" });

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeFalse();
    }

    #endregion

    #region DomainsOverlap Tests

    [Fact]
    public void DomainsOverlap_ExactMatch_ReturnsTrue()
    {
        var domains1 = new List<string> { "example.com" };
        var domains2 = new List<string> { "example.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeTrue();
    }

    [Fact]
    public void DomainsOverlap_CaseInsensitive_ReturnsTrue()
    {
        var domains1 = new List<string> { "EXAMPLE.COM" };
        var domains2 = new List<string> { "example.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeTrue();
    }

    [Fact]
    public void DomainsOverlap_SubdomainMatch_ReturnsTrue()
    {
        var domains1 = new List<string> { "api.example.com" };
        var domains2 = new List<string> { "example.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeTrue();
    }

    [Fact]
    public void DomainsOverlap_ParentDomainMatch_ReturnsTrue()
    {
        var domains1 = new List<string> { "example.com" };
        var domains2 = new List<string> { "sub.example.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeTrue();
    }

    [Fact]
    public void DomainsOverlap_DifferentDomains_ReturnsFalse()
    {
        var domains1 = new List<string> { "example.com" };
        var domains2 = new List<string> { "other.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeFalse();
    }

    [Fact]
    public void DomainsOverlap_SimilarButNotSubdomain_ReturnsFalse()
    {
        // "notexample.com" should NOT match "example.com"
        var domains1 = new List<string> { "notexample.com" };
        var domains2 = new List<string> { "example.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeFalse();
    }

    [Fact]
    public void DomainsOverlap_MultipleDomainsOneMatch_ReturnsTrue()
    {
        var domains1 = new List<string> { "a.com", "b.com", "c.com" };
        var domains2 = new List<string> { "x.com", "b.com", "y.com" };

        FirewallRuleOverlapDetector.DomainsOverlap(domains1, domains2).Should().BeTrue();
    }

    #endregion

    #region PortsOverlap Tests

    [Fact]
    public void PortsOverlap_BothNull_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: null);
        var rule2 = CreateRule(protocol: "tcp", destPort: null);

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_OneNull_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: null);
        var rule2 = CreateRule(protocol: "tcp", destPort: "80");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_SamePort_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "443");
        var rule2 = CreateRule(protocol: "tcp", destPort: "443");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_DifferentPorts_ReturnsFalse()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80");
        var rule2 = CreateRule(protocol: "tcp", destPort: "443");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void PortsOverlap_CommaSeparatedWithOverlap_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80,443,8080");
        var rule2 = CreateRule(protocol: "tcp", destPort: "443,8443");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_CommaSeparatedNoOverlap_ReturnsFalse()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80,8080");
        var rule2 = CreateRule(protocol: "tcp", destPort: "443,8443");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void PortsOverlap_RangeOverlap_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80-100");
        var rule2 = CreateRule(protocol: "tcp", destPort: "90");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_RangeNoOverlap_ReturnsFalse()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80-100");
        var rule2 = CreateRule(protocol: "tcp", destPort: "200");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void PortsOverlap_RangeToRangeOverlap_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80-100");
        var rule2 = CreateRule(protocol: "tcp", destPort: "90-110");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_MixedFormatOverlap_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80,443,8000-8100");
        var rule2 = CreateRule(protocol: "tcp", destPort: "8050");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_NonTcpUdpProtocol_IgnoresPorts()
    {
        // ICMP doesn't use ports, so ports should be ignored
        var rule1 = CreateRule(protocol: "icmp", destPort: "80");
        var rule2 = CreateRule(protocol: "icmp", destPort: "443");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_AllProtocol_IgnoresPorts()
    {
        var rule1 = CreateRule(protocol: "all", destPort: "80");
        var rule2 = CreateRule(protocol: "tcp", destPort: "443");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    #endregion

    #region ParsePortString Tests

    [Fact]
    public void ParsePortString_SinglePort_ReturnsCorrectSet()
    {
        var result = FirewallRuleOverlapDetector.ParsePortString("80");

        result.Should().BeEquivalentTo(new[] { 80 });
    }

    [Fact]
    public void ParsePortString_CommaSeparated_ReturnsCorrectSet()
    {
        var result = FirewallRuleOverlapDetector.ParsePortString("80,443,8080");

        result.Should().BeEquivalentTo(new[] { 80, 443, 8080 });
    }

    [Fact]
    public void ParsePortString_Range_ReturnsCorrectSet()
    {
        var result = FirewallRuleOverlapDetector.ParsePortString("80-83");

        result.Should().BeEquivalentTo(new[] { 80, 81, 82, 83 });
    }

    [Fact]
    public void ParsePortString_MixedFormat_ReturnsCorrectSet()
    {
        var result = FirewallRuleOverlapDetector.ParsePortString("22,80-82,443");

        result.Should().BeEquivalentTo(new[] { 22, 80, 81, 82, 443 });
    }

    [Fact]
    public void ParsePortString_WithSpaces_ReturnsCorrectSet()
    {
        var result = FirewallRuleOverlapDetector.ParsePortString("80, 443, 8080");

        result.Should().BeEquivalentTo(new[] { 80, 443, 8080 });
    }

    #endregion

    #region IcmpTypesOverlap Tests

    [Fact]
    public void IcmpTypesOverlap_NonIcmpProtocol_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", icmpTypename: "ECHO_REQUEST");
        var rule2 = CreateRule(protocol: "tcp", icmpTypename: "ECHO_REPLY");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void IcmpTypesOverlap_BothAny_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "icmp", icmpTypename: "ANY");
        var rule2 = CreateRule(protocol: "icmp", icmpTypename: "ANY");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void IcmpTypesOverlap_OneAny_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "icmp", icmpTypename: "ANY");
        var rule2 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REQUEST");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void IcmpTypesOverlap_SameType_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REQUEST");
        var rule2 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REQUEST");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void IcmpTypesOverlap_DifferentTypes_ReturnsFalse()
    {
        var rule1 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REQUEST");
        var rule2 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REPLY");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void IcmpTypesOverlap_NullTreatedAsAny_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "icmp", icmpTypename: null);
        var rule2 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REQUEST");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void IcmpTypesOverlap_OneRuleAllProtocol_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "all", icmpTypename: null);
        var rule2 = CreateRule(protocol: "icmp", icmpTypename: "ECHO_REQUEST");

        FirewallRuleOverlapDetector.IcmpTypesOverlap(rule1, rule2).Should().BeTrue();
    }

    #endregion

    #region IpRangesOverlap Tests

    [Fact]
    public void IpRangesOverlap_ExactMatch_ReturnsTrue()
    {
        var ips1 = new List<string> { "192.168.1.1" };
        var ips2 = new List<string> { "192.168.1.1" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeTrue();
    }

    [Fact]
    public void IpRangesOverlap_DifferentIps_ReturnsFalse()
    {
        var ips1 = new List<string> { "192.168.1.1" };
        var ips2 = new List<string> { "192.168.1.2" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeFalse();
    }

    [Fact]
    public void IpRangesOverlap_IpInCidr_ReturnsTrue()
    {
        var ips1 = new List<string> { "192.168.1.50" };
        var ips2 = new List<string> { "192.168.1.0/24" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeTrue();
    }

    [Fact]
    public void IpRangesOverlap_IpOutsideCidr_ReturnsFalse()
    {
        var ips1 = new List<string> { "192.168.2.50" };
        var ips2 = new List<string> { "192.168.1.0/24" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeFalse();
    }

    [Fact]
    public void IpRangesOverlap_OverlappingCidrs_ReturnsTrue()
    {
        var ips1 = new List<string> { "192.168.0.0/16" };
        var ips2 = new List<string> { "192.168.1.0/24" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeTrue();
    }

    [Fact]
    public void IpRangesOverlap_NonOverlappingCidrs_ReturnsFalse()
    {
        var ips1 = new List<string> { "192.168.1.0/24" };
        var ips2 = new List<string> { "10.0.0.0/8" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeFalse();
    }

    [Fact]
    public void IpRangesOverlap_MultipleIpsOneMatch_ReturnsTrue()
    {
        var ips1 = new List<string> { "192.168.1.1", "192.168.1.2", "192.168.1.3" };
        var ips2 = new List<string> { "10.0.0.1", "192.168.1.2", "172.16.0.1" };

        FirewallRuleOverlapDetector.IpRangesOverlap(ips1, ips2).Should().BeTrue();
    }

    #endregion

    #region IpMatchesCidr Tests

    [Fact]
    public void IpMatchesCidr_IpInCidr_ReturnsTrue()
    {
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.1.50", "192.168.1.0/24").Should().BeTrue();
    }

    [Fact]
    public void IpMatchesCidr_IpOutsideCidr_ReturnsFalse()
    {
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.2.50", "192.168.1.0/24").Should().BeFalse();
    }

    [Fact]
    public void IpMatchesCidr_IpAtNetworkBoundary_ReturnsTrue()
    {
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.1.0", "192.168.1.0/24").Should().BeTrue();
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.1.255", "192.168.1.0/24").Should().BeTrue();
    }

    [Fact]
    public void IpMatchesCidr_Slash16_ReturnsTrue()
    {
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.50.100", "192.168.0.0/16").Should().BeTrue();
    }

    [Fact]
    public void IpMatchesCidr_Slash8_ReturnsTrue()
    {
        FirewallRuleOverlapDetector.IpMatchesCidr("10.50.100.200", "10.0.0.0/8").Should().BeTrue();
    }

    [Fact]
    public void IpMatchesCidr_CidrInCidr_ReturnsTrue()
    {
        // Smaller CIDR within larger CIDR
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.1.0/24", "192.168.0.0/16").Should().BeTrue();
    }

    [Fact]
    public void IpMatchesCidr_NotCidr_ReturnsFalse()
    {
        // Second argument is not a CIDR
        FirewallRuleOverlapDetector.IpMatchesCidr("192.168.1.50", "192.168.1.1").Should().BeFalse();
    }

    #endregion

    #region RulesOverlap Integration Tests

    [Fact]
    public void RulesOverlap_IdenticalRules_ReturnsTrue()
    {
        var rule1 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "net1" },
            destMatchingTarget: "WEB",
            webDomains: new List<string> { "example.com" },
            destPort: "443");
        var rule2 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "net1" },
            destMatchingTarget: "WEB",
            webDomains: new List<string> { "example.com" },
            destPort: "443");

        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void RulesOverlap_DifferentProtocols_ReturnsFalse()
    {
        var rule1 = CreateRule(protocol: "tcp", destMatchingTarget: "ANY");
        var rule2 = CreateRule(protocol: "udp", destMatchingTarget: "ANY");

        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void RulesOverlap_DifferentDestTypes_ReturnsFalse()
    {
        var rule1 = CreateRule(
            protocol: "tcp",
            destMatchingTarget: "WEB",
            webDomains: new List<string> { "scam.com" });
        var rule2 = CreateRule(
            protocol: "tcp",
            destMatchingTarget: "NETWORK",
            destNetworkIds: new List<string> { "mgmt-network" });

        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void RulesOverlap_WebVsIcmp_ReturnsFalse()
    {
        // "Block Scam Domains" (WEB) vs "Allow Management Ping" (ICMP/NETWORK) - should NOT overlap
        var blockScamDomains = CreateRule(
            protocol: "all",
            destMatchingTarget: "WEB",
            webDomains: new List<string> { "scam.com", "phishing.com" });
        var allowPing = CreateRule(
            protocol: "icmp",
            icmpTypename: "ECHO_REQUEST",
            destMatchingTarget: "NETWORK",
            destNetworkIds: new List<string> { "mgmt-network" });

        FirewallRuleOverlapDetector.RulesOverlap(blockScamDomains, allowPing).Should().BeFalse();
    }

    [Fact]
    public void RulesOverlap_BroadAllowVsSpecificDeny_ReturnsTrue()
    {
        // Broad "Allow All" rule overlaps with specific deny
        var allowAll = CreateRule(
            protocol: "all",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "ANY");
        var denySpecific = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "guest" },
            destMatchingTarget: "NETWORK",
            destNetworkIds: new List<string> { "corporate" },
            destPort: "22");

        FirewallRuleOverlapDetector.RulesOverlap(allowAll, denySpecific).Should().BeTrue();
    }

    [Fact]
    public void RulesOverlap_DifferentPorts_ReturnsFalse()
    {
        var rule1 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "ANY",
            destPort: "80");
        var rule2 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "ANY",
            destPort: "443");

        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void RulesOverlap_DifferentSourceNetworks_ReturnsFalse()
    {
        var rule1 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "guest" },
            destMatchingTarget: "ANY");
        var rule2 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "iot" },
            destMatchingTarget: "ANY");

        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeFalse();
    }

    #endregion

    #region ZonesOverlap Tests

    [Fact]
    public void ZonesOverlap_BothNoZones_ReturnsTrue()
    {
        var rule1 = CreateRule();
        var rule2 = CreateRule();

        FirewallRuleOverlapDetector.ZonesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void ZonesOverlap_SameSourceZone_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceZoneId: "zone-abc");
        var rule2 = CreateRule(sourceZoneId: "zone-abc");

        FirewallRuleOverlapDetector.ZonesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void ZonesOverlap_DifferentSourceZones_ReturnsFalse()
    {
        var rule1 = CreateRule(sourceZoneId: "zone-abc");
        var rule2 = CreateRule(sourceZoneId: "zone-xyz");

        FirewallRuleOverlapDetector.ZonesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void ZonesOverlap_SameDestZone_ReturnsTrue()
    {
        var rule1 = CreateRule(destZoneId: "zone-abc");
        var rule2 = CreateRule(destZoneId: "zone-abc");

        FirewallRuleOverlapDetector.ZonesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void ZonesOverlap_DifferentDestZones_ReturnsFalse()
    {
        var rule1 = CreateRule(destZoneId: "zone-abc");
        var rule2 = CreateRule(destZoneId: "zone-xyz");

        FirewallRuleOverlapDetector.ZonesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void ZonesOverlap_OneHasZoneOneDoesNot_ReturnsTrue()
    {
        var rule1 = CreateRule(sourceZoneId: "zone-abc");
        var rule2 = CreateRule();

        FirewallRuleOverlapDetector.ZonesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void RulesOverlap_DifferentZones_ReturnsFalse()
    {
        var rule1 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "ANY",
            destZoneId: "zone-e0fa");
        var rule2 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "ANY",
            destZoneId: "zone-e0fb");

        // Even though everything else matches, different zones = no overlap
        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeFalse();
    }

    #endregion

    #region MatchOpposite Tests - Sources

    [Fact]
    public void SourcesOverlap_BothNormalWithIntersection_ReturnsTrue()
    {
        var rule1 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.10", "192.168.1.20" });
        var rule2 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.20", "192.168.1.30" });

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_BothInverted_AlwaysReturnsTrue()
    {
        // When both have match_opposite=true, they both match "everyone EXCEPT their list"
        // This always overlaps (unless their lists cover everything)
        var rule1 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.10" },
            sourceMatchOppositeIps: true);
        var rule2 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.20" },
            sourceMatchOppositeIps: true);

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_OneInvertedAllNormalIpsInException_ReturnsFalse()
    {
        // Rule1: Match IPs [A, B], opposite=false -> matches A, B
        // Rule2: Match IPs [A, B, C], opposite=true -> matches everyone EXCEPT A, B, C
        // Since A, B are in the exception list, NO overlap
        var rule1 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.10", "192.168.1.20" },
            sourceMatchOppositeIps: false);
        var rule2 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.10", "192.168.1.20", "192.168.1.30" },
            sourceMatchOppositeIps: true);

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void SourcesOverlap_OneInvertedSomeNormalIpsNotInException_ReturnsTrue()
    {
        // Rule1: Match IPs [A, B], opposite=false -> matches A, B
        // Rule2: Match IPs [C, D], opposite=true -> matches everyone EXCEPT C, D
        // A and B are NOT in exception list, so they overlap
        var rule1 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.10", "192.168.1.20" },
            sourceMatchOppositeIps: false);
        var rule2 = CreateRule(
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.30", "192.168.1.40" },
            sourceMatchOppositeIps: true);

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void SourcesOverlap_NetworksOneInvertedNoOverlap_ReturnsFalse()
    {
        // Rule1: Match networks [guest], opposite=false -> matches guest
        // Rule2: Match networks [guest, iot], opposite=true -> matches everyone EXCEPT guest, iot
        // guest is in the exception list, so NO overlap
        var rule1 = CreateRule(
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "guest" },
            sourceMatchOppositeNetworks: false);
        var rule2 = CreateRule(
            sourceMatchingTarget: "NETWORK",
            sourceNetworkIds: new List<string> { "guest", "iot" },
            sourceMatchOppositeNetworks: true);

        FirewallRuleOverlapDetector.SourcesOverlap(rule1, rule2).Should().BeFalse();
    }

    #endregion

    #region MatchOpposite Tests - Destinations

    [Fact]
    public void DestinationsOverlap_BothInverted_AlwaysReturnsTrue()
    {
        var rule1 = CreateRule(
            destMatchingTarget: "IP",
            destIps: new List<string> { "10.0.0.1" },
            destMatchOppositeIps: true);
        var rule2 = CreateRule(
            destMatchingTarget: "IP",
            destIps: new List<string> { "10.0.0.2" },
            destMatchOppositeIps: true);

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void DestinationsOverlap_OneInvertedNoOverlap_ReturnsFalse()
    {
        // Rule1: Matches 10.0.0.1 only
        // Rule2: Matches everyone EXCEPT 10.0.0.1, 10.0.0.2
        // 10.0.0.1 is in exception, NO overlap
        var rule1 = CreateRule(
            destMatchingTarget: "IP",
            destIps: new List<string> { "10.0.0.1" },
            destMatchOppositeIps: false);
        var rule2 = CreateRule(
            destMatchingTarget: "IP",
            destIps: new List<string> { "10.0.0.1", "10.0.0.2" },
            destMatchOppositeIps: true);

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void DestinationsOverlap_NetworksInvertedWithOverlap_ReturnsTrue()
    {
        // Rule1: Matches management network
        // Rule2: Matches everyone EXCEPT iot (management is NOT excepted)
        var rule1 = CreateRule(
            destMatchingTarget: "NETWORK",
            destNetworkIds: new List<string> { "management" },
            destMatchOppositeNetworks: false);
        var rule2 = CreateRule(
            destMatchingTarget: "NETWORK",
            destNetworkIds: new List<string> { "iot" },
            destMatchOppositeNetworks: true);

        FirewallRuleOverlapDetector.DestinationsOverlap(rule1, rule2).Should().BeTrue();
    }

    #endregion

    #region MatchOpposite Tests - Ports

    [Fact]
    public void PortsOverlap_BothNormalWithIntersection_ReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80,443");
        var rule2 = CreateRule(protocol: "tcp", destPort: "443,8080");

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_BothInverted_AlwaysReturnsTrue()
    {
        var rule1 = CreateRule(protocol: "tcp", destPort: "80", destMatchOppositePorts: true);
        var rule2 = CreateRule(protocol: "tcp", destPort: "443", destMatchOppositePorts: true);

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    [Fact]
    public void PortsOverlap_OneInvertedAllPortsInException_ReturnsFalse()
    {
        // Rule1: Matches ports 80, 443
        // Rule2: Matches all ports EXCEPT 80, 443, 8080
        // 80 and 443 are in exception, NO overlap
        var rule1 = CreateRule(protocol: "tcp", destPort: "80,443", destMatchOppositePorts: false);
        var rule2 = CreateRule(protocol: "tcp", destPort: "80,443,8080", destMatchOppositePorts: true);

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeFalse();
    }

    [Fact]
    public void PortsOverlap_OneInvertedSomePortsNotInException_ReturnsTrue()
    {
        // Rule1: Matches ports 80, 443, 8080
        // Rule2: Matches all ports EXCEPT 80, 443
        // 8080 is NOT in exception, so they overlap
        var rule1 = CreateRule(protocol: "tcp", destPort: "80,443,8080", destMatchOppositePorts: false);
        var rule2 = CreateRule(protocol: "tcp", destPort: "80,443", destMatchOppositePorts: true);

        FirewallRuleOverlapDetector.PortsOverlap(rule1, rule2).Should().BeTrue();
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public void RulesOverlap_RealScenario_AllowVsRestrictWithOppositeIps_NoOverlap()
    {
        // Simulating: "Allow Select Access to Custom UniFi APIs" vs "Restrict CM Access"
        // Allow: Source IPs [192.168.1.220, 192.168.1.10], opposite=false
        // Deny: Source IPs [192.168.1.10, .220, .70, .11], opposite=TRUE (inverted!)
        // The deny rule exempts the allow rule's source IPs, so no overlap
        var allowRule = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.220", "192.168.1.10" },
            sourceMatchOppositeIps: false,
            destMatchingTarget: "ANY",
            destPort: "8088-8089",
            destZoneId: "zone-e0fb");

        var denyRule = CreateRule(
            protocol: "all",
            sourceMatchingTarget: "IP",
            sourceIps: new List<string> { "192.168.1.10", "192.168.1.220", "192.168.1.70", "192.168.1.11" },
            sourceMatchOppositeIps: true,  // INVERTED - matches everyone EXCEPT these IPs
            destMatchingTarget: "IP",
            destIps: new List<string> { "192.168.100.1" },
            destZoneId: "zone-e0fa");

        // Different zones AND the allow IPs are in the deny's exception list
        FirewallRuleOverlapDetector.RulesOverlap(allowRule, denyRule).Should().BeFalse();
    }

    [Fact]
    public void RulesOverlap_RealScenario_DifferentDestinationZones_NoOverlap()
    {
        // Simulating rules targeting different destination zones
        var rule1 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "ANY",
            destPort: "8088-8089",
            destZoneId: "6761c0bfb8cfe0fa");

        var rule2 = CreateRule(
            protocol: "tcp",
            sourceMatchingTarget: "ANY",
            destMatchingTarget: "IP",
            destIps: new List<string> { "10.110.0.0/16" },
            destZoneId: "6761c0bfb8cfe0fb");

        FirewallRuleOverlapDetector.RulesOverlap(rule1, rule2).Should().BeFalse();
    }

    #endregion

    #region Helper Methods

    private static FirewallRule CreateRule(
        string? protocol = null,
        string? sourceMatchingTarget = null,
        List<string>? sourceNetworkIds = null,
        List<string>? sourceIps = null,
        bool sourceMatchOppositeIps = false,
        bool sourceMatchOppositeNetworks = false,
        string? destMatchingTarget = null,
        List<string>? destNetworkIds = null,
        List<string>? destIps = null,
        bool destMatchOppositeIps = false,
        bool destMatchOppositeNetworks = false,
        List<string>? webDomains = null,
        string? destPort = null,
        bool destMatchOppositePorts = false,
        string? icmpTypename = null,
        string? sourceZoneId = null,
        string? destZoneId = null)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Test Rule",
            Enabled = true,
            Protocol = protocol,
            SourceMatchingTarget = sourceMatchingTarget,
            SourceNetworkIds = sourceNetworkIds,
            SourceIps = sourceIps,
            SourceMatchOppositeIps = sourceMatchOppositeIps,
            SourceMatchOppositeNetworks = sourceMatchOppositeNetworks,
            DestinationMatchingTarget = destMatchingTarget,
            DestinationNetworkIds = destNetworkIds,
            DestinationIps = destIps,
            DestinationMatchOppositeIps = destMatchOppositeIps,
            DestinationMatchOppositeNetworks = destMatchOppositeNetworks,
            WebDomains = webDomains,
            DestinationPort = destPort,
            DestinationMatchOppositePorts = destMatchOppositePorts,
            IcmpTypename = icmpTypename,
            SourceZoneId = sourceZoneId,
            DestinationZoneId = destZoneId
        };
    }

    #endregion
}
