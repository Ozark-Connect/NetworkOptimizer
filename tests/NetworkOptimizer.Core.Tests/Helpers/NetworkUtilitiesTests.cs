using System.Net;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class NetworkUtilitiesTests
{
    #region IsIpInSubnet(string, string?) Tests

    [Theory]
    [InlineData("192.168.1.100", "192.168.1.0/24", true)]
    [InlineData("192.168.1.1", "192.168.1.0/24", true)]
    [InlineData("192.168.1.254", "192.168.1.0/24", true)]
    [InlineData("192.168.2.100", "192.168.1.0/24", false)]
    [InlineData("10.0.0.50", "10.0.0.0/8", true)]
    [InlineData("10.255.255.255", "10.0.0.0/8", true)]
    [InlineData("11.0.0.1", "10.0.0.0/8", false)]
    [InlineData("172.16.5.10", "172.16.0.0/12", true)]
    [InlineData("172.31.255.255", "172.16.0.0/12", true)]
    [InlineData("172.32.0.1", "172.16.0.0/12", false)]
    public void IsIpInSubnet_String_ValidCases(string ip, string subnet, bool expected)
    {
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().Be(expected);
    }

    [Theory]
    [InlineData("192.168.1.100", null)]
    [InlineData("192.168.1.100", "")]
    public void IsIpInSubnet_String_NullOrEmptySubnet_ReturnsFalse(string ip, string? subnet)
    {
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().BeFalse();
    }

    [Theory]
    [InlineData("invalid", "192.168.1.0/24")]
    [InlineData("", "192.168.1.0/24")]
    public void IsIpInSubnet_String_InvalidIp_ReturnsFalse(string ip, string subnet)
    {
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.1.100", "192.168.1.0")]
    [InlineData("192.168.1.100", "192.168.1.0/")]
    [InlineData("192.168.1.100", "192.168.1.0/abc")]
    [InlineData("192.168.1.100", "/24")]
    [InlineData("192.168.1.100", "invalid/24")]
    public void IsIpInSubnet_String_InvalidSubnetFormat_ReturnsFalse(string ip, string subnet)
    {
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().BeFalse();
    }

    [Fact]
    public void IsIpInSubnet_String_SlashZero_MatchesAll()
    {
        // /0 means all IPs match
        NetworkUtilities.IsIpInSubnet("1.2.3.4", "0.0.0.0/0").Should().BeTrue();
        NetworkUtilities.IsIpInSubnet("255.255.255.255", "0.0.0.0/0").Should().BeTrue();
    }

    [Fact]
    public void IsIpInSubnet_String_Slash32_ExactMatch()
    {
        // /32 means exact IP match only
        NetworkUtilities.IsIpInSubnet("192.168.1.1", "192.168.1.1/32").Should().BeTrue();
        NetworkUtilities.IsIpInSubnet("192.168.1.2", "192.168.1.1/32").Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.0/25", true)]    // 0-127 in first half
    [InlineData("192.168.1.127", "192.168.1.0/25", true)]   // Last IP in first half
    [InlineData("192.168.1.128", "192.168.1.0/25", false)]  // First IP in second half - NOT in first half
    [InlineData("192.168.1.128", "192.168.1.128/25", true)] // 128-255 in second half
    [InlineData("192.168.1.127", "192.168.1.128/25", false)]
    public void IsIpInSubnet_String_NonStandardPrefixLengths(string ip, string subnet, bool expected)
    {
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().Be(expected);
    }

    #endregion

    #region IsIpInSubnet(IPAddress, string) Tests

    [Theory]
    [InlineData("192.168.1.100", "192.168.1.0/24", true)]
    [InlineData("192.168.2.100", "192.168.1.0/24", false)]
    public void IsIpInSubnet_IPAddress_ValidCases(string ipStr, string subnet, bool expected)
    {
        var ip = IPAddress.Parse(ipStr);
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().Be(expected);
    }

    #endregion

    #region IsIpInAnySubnet Tests

    [Fact]
    public void IsIpInAnySubnet_IpInFirstSubnet_ReturnsTrue()
    {
        var subnets = new[] { "192.168.1.0/24", "10.0.0.0/8", "172.16.0.0/12" };
        NetworkUtilities.IsIpInAnySubnet("192.168.1.50", subnets).Should().BeTrue();
    }

    [Fact]
    public void IsIpInAnySubnet_IpInLastSubnet_ReturnsTrue()
    {
        var subnets = new[] { "192.168.1.0/24", "10.0.0.0/8", "172.16.0.0/12" };
        NetworkUtilities.IsIpInAnySubnet("172.20.5.10", subnets).Should().BeTrue();
    }

    [Fact]
    public void IsIpInAnySubnet_IpNotInAnySubnet_ReturnsFalse()
    {
        var subnets = new[] { "192.168.1.0/24", "10.0.0.0/8", "172.16.0.0/12" };
        NetworkUtilities.IsIpInAnySubnet("8.8.8.8", subnets).Should().BeFalse();
    }

    [Fact]
    public void IsIpInAnySubnet_EmptySubnetList_ReturnsFalse()
    {
        NetworkUtilities.IsIpInAnySubnet("192.168.1.50", Array.Empty<string>()).Should().BeFalse();
    }

    [Fact]
    public void IsIpInAnySubnet_InvalidIp_ReturnsFalse()
    {
        var subnets = new[] { "192.168.1.0/24" };
        NetworkUtilities.IsIpInAnySubnet("invalid", subnets).Should().BeFalse();
    }

    [Fact]
    public void IsIpInAnySubnet_SubnetListWithNullAndEmpty_SkipsThem()
    {
        var subnets = new[] { null, "", "192.168.1.0/24" };
        NetworkUtilities.IsIpInAnySubnet("192.168.1.50", subnets!).Should().BeTrue();
    }

    [Fact]
    public void IsIpInAnySubnet_ExternalDnsNotInInternalSubnets()
    {
        // This is the actual use case - checking if DNS server is internal
        var internalSubnets = new[]
        {
            "192.168.1.0/24",  // Home
            "192.168.10.0/24", // IoT
            "192.168.20.0/24", // Security
            "10.0.0.0/24"      // Management
        };

        // Cloudflare DNS - should NOT be in any internal subnet
        NetworkUtilities.IsIpInAnySubnet("1.1.1.1", internalSubnets).Should().BeFalse();

        // Google DNS - should NOT be in any internal subnet
        NetworkUtilities.IsIpInAnySubnet("8.8.8.8", internalSubnets).Should().BeFalse();

        // Internal Pi-hole - SHOULD be in internal subnet
        NetworkUtilities.IsIpInAnySubnet("192.168.1.5", internalSubnets).Should().BeTrue();
    }

    #endregion
}
