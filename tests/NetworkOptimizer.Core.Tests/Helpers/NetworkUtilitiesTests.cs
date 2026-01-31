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

    #region IsPrivateIpAddress - IPv6 Tests

    [Theory]
    [InlineData("::1", true)]                             // IPv6 loopback
    [InlineData("fe80::1", true)]                         // Link-local start
    [InlineData("fe80::abcd:1234:5678:9abc", true)]       // Link-local typical
    [InlineData("febf::1", true)]                         // Link-local end of range (fe80::/10)
    [InlineData("fc00::1", true)]                         // Unique local (ULA) start
    [InlineData("fd00::1", true)]                         // Unique local (ULA) - commonly used
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true)] // ULA end
    public void IsPrivateIpAddress_IPv6_PrivateAddresses_ReturnsTrue(string ip, bool expected)
    {
        NetworkUtilities.IsPrivateIpAddress(ip).Should().Be(expected);
    }

    [Theory]
    [InlineData("2001:db8::1")]                           // Documentation range (public/special)
    [InlineData("2001:4860:4860::8888")]                  // Google DNS IPv6
    [InlineData("2606:4700:4700::1111")]                  // Cloudflare DNS IPv6
    [InlineData("2001:470:1:18::119")]                    // Random global unicast
    public void IsPrivateIpAddress_IPv6_PublicAddresses_ReturnsFalse(string ip)
    {
        NetworkUtilities.IsPrivateIpAddress(ip).Should().BeFalse();
    }

    [Fact]
    public void IsPrivateIpAddress_IPv6_Loopback()
    {
        // ::1 is the IPv6 loopback, equivalent to 127.0.0.1
        NetworkUtilities.IsPrivateIpAddress("::1").Should().BeTrue();
        // Full form
        NetworkUtilities.IsPrivateIpAddress("0:0:0:0:0:0:0:1").Should().BeTrue();
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

    #region IsPublicIpAddress - IPv6 Tests

    [Theory]
    [InlineData("2001:4860:4860::8888", true)]            // Google DNS IPv6 - public
    [InlineData("2606:4700:4700::1111", true)]            // Cloudflare DNS IPv6 - public
    [InlineData("2001:470:1:18::119", true)]              // Random global unicast - public
    [InlineData("::1", false)]                            // Loopback - not public
    [InlineData("fe80::1", false)]                        // Link-local - not public
    [InlineData("fd00::1", false)]                        // ULA - not public
    public void IsPublicIpAddress_IPv6_ValidCases(string ip, bool expected)
    {
        NetworkUtilities.IsPublicIpAddress(ip).Should().Be(expected);
    }

    [Fact]
    public void IsPublicIpAddress_IPv6_GlobalUnicast()
    {
        // Global unicast addresses (2000::/3) are public
        NetworkUtilities.IsPublicIpAddress("2001:db8::1").Should().BeTrue();
        NetworkUtilities.IsPublicIpAddress("2607:f8b0:4004:800::200e").Should().BeTrue();
    }

    #endregion

    #region NormalizeIpAddress Tests

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1")]                    // IPv4 unchanged
    [InlineData("192.168.001.001", "192.168.1.1")]                // IPv4 with leading zeros normalizes
    [InlineData("010.000.000.001", "8.0.0.1")]                    // IPv4 with leading zeros - octal (010 octal = 8 decimal)
    [InlineData("2001:db8::1", "2001:db8::1")]                    // IPv6 compressed stays compressed
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")] // IPv6 full form normalizes to compressed
    [InlineData("::1", "::1")]                                    // IPv6 loopback
    [InlineData("0:0:0:0:0:0:0:1", "::1")]                        // IPv6 loopback full form
    [InlineData("fe80:0:0:0:0:0:0:1", "fe80::1")]                 // IPv6 link-local
    public void NormalizeIpAddress_ValidAddresses_NormalizesToCanonicalForm(string input, string expected)
    {
        NetworkUtilities.NormalizeIpAddress(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeIpAddress_NullOrEmpty_ReturnsEmptyOrOriginal(string? input, string expected)
    {
        NetworkUtilities.NormalizeIpAddress(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("not-an-ip")]
    [InlineData("999.999.999.999")]
    public void NormalizeIpAddress_InvalidAddress_ReturnsOriginal(string input)
    {
        NetworkUtilities.NormalizeIpAddress(input).Should().Be(input);
    }

    #endregion

    #region IpAddressesAreEqual Tests

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1", true)]              // IPv4 same
    [InlineData("192.168.1.1", "192.168.001.001", true)]          // IPv4 with leading zeros (works for octets < 8)
    [InlineData("8.0.0.1", "010.000.000.001", true)]              // IPv4 with leading zeros - 010 is octal (8 decimal)
    [InlineData("192.168.1.1", "192.168.1.2", false)]             // IPv4 different
    [InlineData("2001:db8::1", "2001:db8::1", true)]              // IPv6 same compressed
    [InlineData("2001:db8::1", "2001:0db8:0000:0000:0000:0000:0000:0001", true)] // IPv6 different formats
    [InlineData("::1", "0:0:0:0:0:0:0:1", true)]                  // IPv6 loopback different formats
    [InlineData("fe80::1", "fe80:0:0:0:0:0:0:1", true)]           // IPv6 link-local different formats
    [InlineData("2001:db8::1", "2001:db8::2", false)]             // IPv6 different
    [InlineData("192.168.1.1", "2001:db8::1", false)]             // IPv4 vs IPv6
    public void IpAddressesAreEqual_ValidAddresses_ReturnsCorrectResult(string ip1, string ip2, bool expected)
    {
        NetworkUtilities.IpAddressesAreEqual(ip1, ip2).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "192.168.1.1")]
    [InlineData("192.168.1.1", null)]
    [InlineData(null, null)]
    [InlineData("", "192.168.1.1")]
    [InlineData("192.168.1.1", "")]
    [InlineData("invalid", "192.168.1.1")]
    [InlineData("192.168.1.1", "invalid")]
    public void IpAddressesAreEqual_NullOrInvalid_ReturnsFalse(string? ip1, string? ip2)
    {
        NetworkUtilities.IpAddressesAreEqual(ip1, ip2).Should().BeFalse();
    }

    #endregion

    #region CidrCoversSubnet Tests

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24", true)]   // Exact match
    [InlineData("192.168.0.0/16", "192.168.1.0/24", true)]   // Larger covers smaller
    [InlineData("10.0.0.0/8", "10.1.2.0/24", true)]          // Class A covers /24
    [InlineData("192.168.1.0/24", "192.168.0.0/16", false)]  // Smaller doesn't cover larger
    [InlineData("192.168.1.0/24", "192.168.2.0/24", false)]  // Different network
    [InlineData("0.0.0.0/0", "192.168.1.0/24", true)]        // /0 covers everything
    public void CidrCoversSubnet_IPv4_ValidCases(string outer, string inner, bool expected)
    {
        NetworkUtilities.CidrCoversSubnet(outer, inner).Should().Be(expected);
    }

    [Theory]
    [InlineData("2001:db8::/32", "2001:db8:abcd::/48", true)]   // IPv6 larger covers smaller
    [InlineData("2001:db8:abcd::/48", "2001:db8:abcd:1234::/64", true)]
    [InlineData("2001:db8:abcd:1234::/64", "2001:db8:abcd::/48", false)]
    [InlineData("::/0", "2001:db8::/32", true)]  // /0 covers everything
    public void CidrCoversSubnet_IPv6_ValidCases(string outer, string inner, bool expected)
    {
        NetworkUtilities.CidrCoversSubnet(outer, inner).Should().Be(expected);
    }

    [Fact]
    public void CidrCoversSubnet_MixedFamilies_ReturnsFalse()
    {
        NetworkUtilities.CidrCoversSubnet("192.168.0.0/16", "2001:db8::/32").Should().BeFalse();
        NetworkUtilities.CidrCoversSubnet("2001:db8::/32", "192.168.1.0/24").Should().BeFalse();
    }

    [Theory]
    [InlineData("invalid", "192.168.1.0/24")]
    [InlineData("192.168.1.0/24", "invalid")]
    [InlineData("", "192.168.1.0/24")]
    [InlineData("192.168.1.0", "192.168.1.0/24")]  // Missing prefix
    public void CidrCoversSubnet_InvalidInput_ReturnsFalse(string outer, string inner)
    {
        NetworkUtilities.CidrCoversSubnet(outer, inner).Should().BeFalse();
    }

    #endregion

    #region ExpandIpRange Tests

    [Fact]
    public void ExpandIpRange_SingleIp_ReturnsSingleIp()
    {
        var result = NetworkUtilities.ExpandIpRange("192.168.1.1");
        result.Should().ContainSingle().Which.Should().Be("192.168.1.1");
    }

    [Fact]
    public void ExpandIpRange_NullOrEmpty_ReturnsEmptyList()
    {
        NetworkUtilities.ExpandIpRange(null).Should().BeEmpty();
        NetworkUtilities.ExpandIpRange("").Should().BeEmpty();
    }

    [Fact]
    public void ExpandIpRange_ValidRange_ReturnsAllIps()
    {
        var result = NetworkUtilities.ExpandIpRange("192.168.1.10-192.168.1.12");
        result.Should().HaveCount(3);
        result.Should().Contain("192.168.1.10");
        result.Should().Contain("192.168.1.11");
        result.Should().Contain("192.168.1.12");
    }

    [Fact]
    public void ExpandIpRange_TwoIpRange_ReturnsBothIps()
    {
        var result = NetworkUtilities.ExpandIpRange("172.16.1.253-172.16.1.254");
        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(new[] { "172.16.1.253", "172.16.1.254" });
    }

    [Fact]
    public void ExpandIpRange_CrossSubnetRange_ReturnsSingleValue()
    {
        // Cross-subnet ranges are not expanded
        var result = NetworkUtilities.ExpandIpRange("192.168.1.1-192.168.2.1");
        result.Should().ContainSingle().Which.Should().Be("192.168.1.1-192.168.2.1");
    }

    [Fact]
    public void ExpandIpRange_ReversedRange_ReturnsSingleValue()
    {
        // Reversed ranges (start > end) are not expanded
        var result = NetworkUtilities.ExpandIpRange("192.168.1.10-192.168.1.5");
        result.Should().ContainSingle().Which.Should().Be("192.168.1.10-192.168.1.5");
    }

    [Fact]
    public void ExpandIpRange_InvalidIp_ReturnsSingleValue()
    {
        var result = NetworkUtilities.ExpandIpRange("not-an-ip");
        result.Should().ContainSingle().Which.Should().Be("not-an-ip");
    }

    #endregion

    #region ParseCidr Tests

    [Fact]
    public void ParseCidr_ValidIPv4_ReturnsNetworkAndPrefix()
    {
        var (network, prefix) = NetworkUtilities.ParseCidr("192.168.1.0/24");
        network.Should().NotBeNull();
        network!.ToString().Should().Be("192.168.1.0");
        prefix.Should().Be(24);
    }

    [Fact]
    public void ParseCidr_ValidIPv6_ReturnsNetworkAndPrefix()
    {
        var (network, prefix) = NetworkUtilities.ParseCidr("2001:db8::/32");
        network.Should().NotBeNull();
        network!.ToString().Should().Be("2001:db8::");
        prefix.Should().Be(32);
    }

    [Theory]
    [InlineData("192.168.1.0")]      // Missing prefix
    [InlineData("192.168.1.0/")]     // Empty prefix
    [InlineData("/24")]              // Missing network
    [InlineData("invalid/24")]       // Invalid IP
    [InlineData("192.168.1.0/abc")]  // Non-numeric prefix
    public void ParseCidr_InvalidInput_ReturnsNull(string cidr)
    {
        var (network, _) = NetworkUtilities.ParseCidr(cidr);
        network.Should().BeNull();
    }

    #endregion

    #region NormalizeControllerUrl Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeControllerUrl_NullOrWhitespace_ReturnsAsIs(string? url)
    {
        NetworkUtilities.NormalizeControllerUrl(url!).Should().Be(url);
    }

    [Theory]
    [InlineData("unifi.example.com", "https://unifi.example.com")]
    [InlineData("192.168.1.1", "https://192.168.1.1")]
    [InlineData("my-controller.local", "https://my-controller.local")]
    public void NormalizeControllerUrl_BareHostname_PrependsHttps(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://unifi.example.com", "https://unifi.example.com")]
    [InlineData("http://unifi.example.com", "http://unifi.example.com")]
    [InlineData("HTTPS://unifi.example.com", "https://unifi.example.com")]
    [InlineData("HTTP://unifi.example.com", "http://unifi.example.com")]
    public void NormalizeControllerUrl_WithScheme_PreservesScheme(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://unifi.example.com/", "https://unifi.example.com")]
    [InlineData("https://unifi.example.com///", "https://unifi.example.com")]
    [InlineData("unifi.example.com/", "https://unifi.example.com")]
    public void NormalizeControllerUrl_TrailingSlash_Removed(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://unifi.example.com/network/default/dashboard", "https://unifi.example.com")]
    [InlineData("https://unifi.example.com/network/default/dashboard/", "https://unifi.example.com")]
    [InlineData("unifi.example.com/network/default", "https://unifi.example.com")]
    [InlineData("http://192.168.1.1/api/s/default", "http://192.168.1.1")]
    public void NormalizeControllerUrl_WithPath_StripsPath(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://unifi.example.com:8443", "https://unifi.example.com:8443")]
    [InlineData("https://unifi.example.com:8443/network/default", "https://unifi.example.com:8443")]
    [InlineData("http://192.168.1.1:8080/api", "http://192.168.1.1:8080")]
    [InlineData("unifi.example.com:8443", "https://unifi.example.com:8443")]
    [InlineData("unifi.example.com:8443/path", "https://unifi.example.com:8443")]
    public void NormalizeControllerUrl_NonDefaultPort_PortPreserved(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://unifi.example.com:443", "https://unifi.example.com")]
    [InlineData("http://unifi.example.com:80", "http://unifi.example.com")]
    public void NormalizeControllerUrl_DefaultPort_PortOmitted(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("  unifi.example.com  ", "https://unifi.example.com")]
    [InlineData("  https://unifi.example.com  ", "https://unifi.example.com")]
    [InlineData("\thttps://unifi.example.com\n", "https://unifi.example.com")]
    public void NormalizeControllerUrl_Whitespace_Trimmed(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://unifi.example.com?query=1", "https://unifi.example.com")]
    [InlineData("https://unifi.example.com/path?query=1#fragment", "https://unifi.example.com")]
    public void NormalizeControllerUrl_QueryAndFragment_Stripped(string input, string expected)
    {
        NetworkUtilities.NormalizeControllerUrl(input).Should().Be(expected);
    }

    #endregion
}
