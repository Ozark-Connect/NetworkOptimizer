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

    #region IsIpInSubnet - IPv6 Tests

    [Theory]
    [InlineData("2001:db8::1", "2001:db8::/32", true)]
    [InlineData("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", "2001:db8::/32", true)]
    [InlineData("2001:db9::1", "2001:db8::/32", false)]
    [InlineData("2001:db8:abcd:1234::1", "2001:db8:abcd:1234::/64", true)]
    [InlineData("2001:db8:abcd:1234:ffff:ffff:ffff:ffff", "2001:db8:abcd:1234::/64", true)]
    [InlineData("2001:db8:abcd:1235::1", "2001:db8:abcd:1234::/64", false)]
    public void IsIpInSubnet_IPv6_ValidCases(string ip, string subnet, bool expected)
    {
        NetworkUtilities.IsIpInSubnet(ip, subnet).Should().Be(expected);
    }

    [Fact]
    public void IsIpInSubnet_IPv6_Slash128_ExactMatch()
    {
        // /128 means exact IPv6 address match only
        NetworkUtilities.IsIpInSubnet("2001:db8::1", "2001:db8::1/128").Should().BeTrue();
        NetworkUtilities.IsIpInSubnet("2001:db8::2", "2001:db8::1/128").Should().BeFalse();
    }

    [Fact]
    public void IsIpInSubnet_IPv6_SlashZero_MatchesAll()
    {
        // /0 means all IPv6 addresses match
        NetworkUtilities.IsIpInSubnet("2001:db8::1", "::/0").Should().BeTrue();
        NetworkUtilities.IsIpInSubnet("fe80::1", "::/0").Should().BeTrue();
    }

    [Fact]
    public void IsIpInSubnet_MixedAddressFamilies_ReturnsFalse()
    {
        // IPv4 address against IPv6 subnet
        NetworkUtilities.IsIpInSubnet("192.168.1.1", "2001:db8::/32").Should().BeFalse();

        // IPv6 address against IPv4 subnet
        NetworkUtilities.IsIpInSubnet("2001:db8::1", "192.168.1.0/24").Should().BeFalse();
    }

    [Fact]
    public void IsIpInSubnet_IPv6_LinkLocal()
    {
        // Link-local addresses (fe80::/10)
        NetworkUtilities.IsIpInSubnet("fe80::1", "fe80::/10").Should().BeTrue();
        NetworkUtilities.IsIpInSubnet("fe80::abcd:1234:5678:9abc", "fe80::/10").Should().BeTrue();
        NetworkUtilities.IsIpInSubnet("2001:db8::1", "fe80::/10").Should().BeFalse();
    }

    [Theory]
    [InlineData("2001:db8::1", "2001:db8::/48", true)]
    [InlineData("2001:db8:0:ffff::1", "2001:db8::/48", true)]
    [InlineData("2001:db8:1::1", "2001:db8::/48", false)]
    public void IsIpInSubnet_IPv6_Slash48(string ip, string subnet, bool expected)
    {
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

    #region IsPrivateIpAddress Tests

    [Theory]
    [InlineData("10.0.0.1", true)]        // RFC1918 Class A
    [InlineData("10.255.255.255", true)]  // RFC1918 Class A edge
    [InlineData("172.16.0.1", true)]      // RFC1918 Class B start
    [InlineData("172.31.255.255", true)]  // RFC1918 Class B end
    [InlineData("192.168.0.1", true)]     // RFC1918 Class C
    [InlineData("192.168.255.255", true)] // RFC1918 Class C edge
    [InlineData("127.0.0.1", true)]       // Loopback
    [InlineData("169.254.1.1", true)]     // Link-local
    [InlineData("100.64.0.1", true)]      // CGNAT start
    [InlineData("100.127.255.255", true)] // CGNAT end
    public void IsPrivateIpAddress_PrivateIps_ReturnsTrue(string ip, bool expected)
    {
        NetworkUtilities.IsPrivateIpAddress(ip).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.1.1.1")]       // Cloudflare
    [InlineData("8.8.8.8")]       // Google
    [InlineData("9.9.9.9")]       // Quad9
    [InlineData("172.15.0.1")]    // Just before RFC1918 Class B
    [InlineData("172.32.0.1")]    // Just after RFC1918 Class B
    [InlineData("100.63.255.255")] // Just before CGNAT
    [InlineData("100.128.0.0")]   // Just after CGNAT
    [InlineData("11.0.0.1")]      // Just after Class A private
    public void IsPrivateIpAddress_PublicIps_ReturnsFalse(string ip)
    {
        NetworkUtilities.IsPrivateIpAddress(ip).Should().BeFalse();
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("256.256.256.256")]
    public void IsPrivateIpAddress_InvalidIp_ReturnsFalse(string ip)
    {
        NetworkUtilities.IsPrivateIpAddress(ip).Should().BeFalse();
    }

    #endregion

    #region IsPublicIpAddress Tests

    [Theory]
    [InlineData("1.1.1.1", true)]         // Cloudflare
    [InlineData("8.8.8.8", true)]         // Google
    [InlineData("192.168.1.1", false)]    // Private
    [InlineData("10.0.0.1", false)]       // Private
    [InlineData("127.0.0.1", false)]      // Loopback
    public void IsPublicIpAddress_ValidCases(string ip, bool expected)
    {
        NetworkUtilities.IsPublicIpAddress(ip).Should().Be(expected);
    }

    [Fact]
    public void IsPublicIpAddress_InvalidIp_ReturnsFalse()
    {
        NetworkUtilities.IsPublicIpAddress("invalid").Should().BeFalse();
    }

    #endregion
}
