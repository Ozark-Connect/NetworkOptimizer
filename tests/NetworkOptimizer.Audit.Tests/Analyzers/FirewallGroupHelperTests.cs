using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class FirewallGroupHelperTests
{
    private readonly Mock<ILogger> _loggerMock = new();

    #region IncludesPort Tests

    [Theory]
    [InlineData("53", "53", true)]
    [InlineData("80", "53", false)]
    [InlineData("53,80,443", "53", true)]
    [InlineData("53,80,443", "80", true)]
    [InlineData("53,80,443", "443", true)]
    [InlineData("53,80,443", "8080", false)]
    [InlineData("", "53", false)]
    [InlineData(null, "53", false)]
    public void IncludesPort_SinglePortsAndLists_ReturnsExpected(string? portSpec, string port, bool expected)
    {
        var result = FirewallGroupHelper.IncludesPort(portSpec, port);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("50-100", "53", true)]
    [InlineData("50-100", "50", true)]
    [InlineData("50-100", "100", true)]
    [InlineData("50-100", "49", false)]
    [InlineData("50-100", "101", false)]
    [InlineData("1-65535", "53", true)]
    [InlineData("1-65535", "443", true)]
    [InlineData("800-900", "853", true)]
    [InlineData("800-900", "799", false)]
    public void IncludesPort_PortRanges_ReturnsExpected(string portSpec, string port, bool expected)
    {
        var result = FirewallGroupHelper.IncludesPort(portSpec, port);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("22,50-100,443", "53", true)]   // 53 in range
    [InlineData("22,50-100,443", "22", true)]   // exact match
    [InlineData("22,50-100,443", "443", true)]  // exact match
    [InlineData("22,50-100,443", "80", true)]   // in range
    [InlineData("22,50-100,443", "200", false)] // not in any
    [InlineData("53,800-900", "853", true)]     // in range
    [InlineData("53,800-900", "53", true)]      // exact match
    public void IncludesPort_MixedPortsAndRanges_ReturnsExpected(string portSpec, string port, bool expected)
    {
        var result = FirewallGroupHelper.IncludesPort(portSpec, port);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("abc", "53", false)]       // Invalid port spec
    [InlineData("53", "abc", false)]       // Invalid target port
    [InlineData("abc-def", "53", false)]   // Invalid range
    [InlineData("-100", "53", false)]      // Malformed range
    [InlineData("100-", "53", false)]      // Malformed range
    public void IncludesPort_InvalidInputs_ReturnsFalse(string? portSpec, string port, bool expected)
    {
        var result = FirewallGroupHelper.IncludesPort(portSpec, port);
        result.Should().Be(expected);
    }

    #endregion

    #region ResolvePortGroup Tests

    [Fact]
    public void ResolvePortGroup_ValidPortGroup_ReturnsCommaSeparatedPorts()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "DNS Ports",
                GroupType = "port-group",
                GroupMembers = new List<string> { "53", "853" }
            }
        };

        var result = FirewallGroupHelper.ResolvePortGroup("group1", groups, _loggerMock.Object);

        result.Should().Be("53,853");
    }

    [Fact]
    public void ResolvePortGroup_PortGroupWithRange_PreservesRange()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "Port Range",
                GroupType = "port-group",
                GroupMembers = new List<string> { "4001-4003" }
            }
        };

        var result = FirewallGroupHelper.ResolvePortGroup("group1", groups, _loggerMock.Object);

        result.Should().Be("4001-4003");
    }

    [Fact]
    public void ResolvePortGroup_NonexistentGroup_ReturnsNull()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>();

        var result = FirewallGroupHelper.ResolvePortGroup("nonexistent", groups, _loggerMock.Object);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolvePortGroup_WrongGroupType_ReturnsNull()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "IP Group",
                GroupType = "address-group",
                GroupMembers = new List<string> { "192.168.1.1" }
            }
        };

        var result = FirewallGroupHelper.ResolvePortGroup("group1", groups, _loggerMock.Object);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolvePortGroup_EmptyGroupMembers_ReturnsNull()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "Empty Group",
                GroupType = "port-group",
                GroupMembers = new List<string>()
            }
        };

        var result = FirewallGroupHelper.ResolvePortGroup("group1", groups, _loggerMock.Object);

        result.Should().BeNull();
    }

    [Fact]
    public void ResolvePortGroup_NullGroups_ReturnsNull()
    {
        var result = FirewallGroupHelper.ResolvePortGroup("group1", null, _loggerMock.Object);

        result.Should().BeNull();
    }

    #endregion

    #region ResolveAddressGroup Tests

    [Fact]
    public void ResolveAddressGroup_ValidAddressGroup_ReturnsIpList()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "Admin IPs",
                GroupType = "address-group",
                GroupMembers = new List<string> { "192.168.1.10", "192.168.1.11" }
            }
        };

        var result = FirewallGroupHelper.ResolveAddressGroup("group1", groups, _loggerMock.Object);

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().Contain("192.168.1.10");
        result.Should().Contain("192.168.1.11");
    }

    [Fact]
    public void ResolveAddressGroup_IPv6AddressGroup_ReturnsIpList()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "IPv6 Servers",
                GroupType = "ipv6-address-group",
                GroupMembers = new List<string> { "2001:db8::1", "2001:db8::2" }
            }
        };

        var result = FirewallGroupHelper.ResolveAddressGroup("group1", groups, _loggerMock.Object);

        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().Contain("2001:db8::1");
    }

    [Fact]
    public void ResolveAddressGroup_WithCidr_PreservesCidr()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "Subnets",
                GroupType = "address-group",
                GroupMembers = new List<string> { "192.168.1.0/24", "10.0.0.0/8" }
            }
        };

        var result = FirewallGroupHelper.ResolveAddressGroup("group1", groups, _loggerMock.Object);

        result.Should().Contain("192.168.1.0/24");
        result.Should().Contain("10.0.0.0/8");
    }

    [Fact]
    public void ResolveAddressGroup_PortGroup_ReturnsNull()
    {
        var groups = new Dictionary<string, UniFiFirewallGroup>
        {
            ["group1"] = new UniFiFirewallGroup
            {
                Id = "group1",
                Name = "Ports",
                GroupType = "port-group",
                GroupMembers = new List<string> { "53" }
            }
        };

        var result = FirewallGroupHelper.ResolveAddressGroup("group1", groups, _loggerMock.Object);

        result.Should().BeNull();
    }

    #endregion

    #region AllowsProtocol Tests

    [Theory]
    [InlineData("tcp", false, "tcp", true)]     // tcp allows tcp
    [InlineData("tcp", false, "udp", false)]    // tcp doesn't allow udp
    [InlineData("udp", false, "udp", true)]     // udp allows udp
    [InlineData("udp", false, "tcp", false)]    // udp doesn't allow tcp
    [InlineData("tcp_udp", false, "tcp", true)] // tcp_udp allows tcp
    [InlineData("tcp_udp", false, "udp", true)] // tcp_udp allows udp
    [InlineData("all", false, "tcp", true)]     // all allows tcp
    [InlineData("all", false, "udp", true)]     // all allows udp
    [InlineData(null, false, "tcp", true)]      // null defaults to all
    public void AllowsProtocol_NormalMode_ReturnsExpected(string? ruleProtocol, bool matchOpposite, string target, bool expected)
    {
        var result = FirewallGroupHelper.AllowsProtocol(ruleProtocol, matchOpposite, target);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("tcp", true, "tcp", false)]    // opposite tcp excludes tcp
    [InlineData("tcp", true, "udp", true)]     // opposite tcp allows udp
    [InlineData("udp", true, "udp", false)]    // opposite udp excludes udp
    [InlineData("udp", true, "tcp", true)]     // opposite udp allows tcp
    [InlineData("icmp", true, "tcp", true)]    // opposite icmp allows tcp
    [InlineData("icmp", true, "udp", true)]    // opposite icmp allows udp
    public void AllowsProtocol_OppositeMode_ReturnsExpected(string ruleProtocol, bool matchOpposite, string target, bool expected)
    {
        var result = FirewallGroupHelper.AllowsProtocol(ruleProtocol, matchOpposite, target);
        result.Should().Be(expected);
    }

    #endregion

    #region BlocksProtocol Tests

    [Theory]
    [InlineData("udp", false, "udp", true)]     // udp blocks udp
    [InlineData("udp", false, "tcp", false)]    // udp doesn't block tcp
    [InlineData("tcp", false, "tcp", true)]     // tcp blocks tcp
    [InlineData("tcp", false, "udp", false)]    // tcp doesn't block udp
    [InlineData("tcp_udp", false, "tcp", true)] // tcp_udp blocks tcp
    [InlineData("tcp_udp", false, "udp", true)] // tcp_udp blocks udp
    [InlineData("all", false, "udp", true)]     // all blocks udp
    public void BlocksProtocol_NormalMode_ReturnsExpected(string ruleProtocol, bool matchOpposite, string target, bool expected)
    {
        var result = FirewallGroupHelper.BlocksProtocol(ruleProtocol, matchOpposite, target);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("udp", true, "udp", false)]    // opposite udp excludes udp from blocking
    [InlineData("udp", true, "tcp", true)]     // opposite udp blocks tcp
    [InlineData("tcp", true, "tcp", false)]    // opposite tcp excludes tcp from blocking
    [InlineData("tcp", true, "udp", true)]     // opposite tcp blocks udp
    [InlineData("icmp", true, "tcp", true)]    // opposite icmp blocks tcp
    [InlineData("icmp", true, "udp", true)]    // opposite icmp blocks udp
    public void BlocksProtocol_OppositeMode_ReturnsExpected(string ruleProtocol, bool matchOpposite, string target, bool expected)
    {
        var result = FirewallGroupHelper.BlocksProtocol(ruleProtocol, matchOpposite, target);
        result.Should().Be(expected);
    }

    #endregion

    #region RuleAllowsPortAndProtocol Tests

    [Fact]
    public void RuleAllowsPortAndProtocol_MatchingPortAndProtocol_ReturnsTrue()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "123",
            Protocol = "udp",
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "123", "udp");

        result.Should().BeTrue();
    }

    [Fact]
    public void RuleAllowsPortAndProtocol_WrongPort_ReturnsFalse()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "80",
            Protocol = "tcp",
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "443", "tcp");

        result.Should().BeFalse();
    }

    [Fact]
    public void RuleAllowsPortAndProtocol_WrongProtocol_ReturnsFalse()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "123",
            Protocol = "tcp", // NTP needs UDP
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "123", "udp");

        result.Should().BeFalse();
    }

    [Fact]
    public void RuleAllowsPortAndProtocol_InvertedPorts_ReturnsFalse()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "123",
            Protocol = "udp",
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = true // Port is inverted (all EXCEPT 123)
        };

        var result = FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "123", "udp");

        result.Should().BeFalse();
    }

    [Fact]
    public void RuleAllowsPortAndProtocol_InvertedProtocol_ReturnsFalse()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "123",
            Protocol = "udp",
            MatchOppositeProtocol = true, // All EXCEPT UDP
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "123", "udp");

        result.Should().BeFalse();
    }

    [Fact]
    public void RuleAllowsPortAndProtocol_PortRange_ReturnsTrue()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "100-150",
            Protocol = "udp",
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleAllowsPortAndProtocol(rule, "123", "udp");

        result.Should().BeTrue();
    }

    #endregion

    #region RuleBlocksPortAndProtocol Tests

    [Fact]
    public void RuleBlocksPortAndProtocol_MatchingPortAndProtocol_ReturnsTrue()
    {
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "53",
            Protocol = "udp",
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleBlocksPortAndProtocol(rule, "53", "udp");

        result.Should().BeTrue();
    }

    [Fact]
    public void RuleBlocksPortAndProtocol_InvertedPorts_ReturnsFalse()
    {
        // match_opposite_ports=true with port 53 means "block all EXCEPT port 53"
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "53",
            Protocol = "udp",
            MatchOppositeProtocol = false,
            DestinationMatchOppositePorts = true
        };

        var result = FirewallGroupHelper.RuleBlocksPortAndProtocol(rule, "53", "udp");

        result.Should().BeFalse();
    }

    [Fact]
    public void RuleBlocksPortAndProtocol_InvertedProtocolIcmp_BlocksUdp()
    {
        // match_opposite_protocol=true with protocol=icmp means "block all EXCEPT ICMP"
        // So UDP IS blocked
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "53",
            Protocol = "icmp",
            MatchOppositeProtocol = true,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleBlocksPortAndProtocol(rule, "53", "udp");

        result.Should().BeTrue();
    }

    [Fact]
    public void RuleBlocksPortAndProtocol_InvertedProtocolUdp_DoesNotBlockUdp()
    {
        // match_opposite_protocol=true with protocol=udp means "block all EXCEPT UDP"
        // So UDP is NOT blocked
        var rule = new NetworkOptimizer.Audit.Models.FirewallRule
        {
            Id = "test-rule",
            DestinationPort = "53",
            Protocol = "udp",
            MatchOppositeProtocol = true,
            DestinationMatchOppositePorts = false
        };

        var result = FirewallGroupHelper.RuleBlocksPortAndProtocol(rule, "53", "udp");

        result.Should().BeFalse();
    }

    #endregion
}
