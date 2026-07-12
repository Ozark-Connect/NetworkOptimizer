using System.Net;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class ProxyDialPolicyTests
{
    #region Default (site-local) policy

    [Theory]
    // RFC1918
    [InlineData("10.20.30.1", true)]
    [InlineData("172.16.5.10", true)]
    [InlineData("192.168.77.1", true)]
    // IPv6 unique-local and link-local
    [InlineData("fd00::1", true)]
    [InlineData("fe80::1", true)]
    // Public
    [InlineData("203.0.113.5", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    // Loopback is not a proxy target (console/gateway/devices are never the agent itself)
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    // IPv4 link-local and CGNAT: reviewed and excluded from the default fence
    [InlineData("169.254.10.10", false)]
    [InlineData("100.64.0.1", false)]
    // Multicast / unspecified
    [InlineData("224.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    public void SiteLocal_AllowsOnlySiteLocalAddresses(string ip, bool expected)
    {
        ProxyDialPolicy.SiteLocal.IsAllowed(IPAddress.Parse(ip)).Should().Be(expected);
    }

    [Fact]
    public void SiteLocal_UnwrapsIPv4MappedIPv6()
    {
        // A public IPv4 must not slip through as ::ffff:a.b.c.d
        ProxyDialPolicy.SiteLocal.IsAllowed(IPAddress.Parse("::ffff:203.0.113.5")).Should().BeFalse();
        ProxyDialPolicy.SiteLocal.IsAllowed(IPAddress.Parse("::ffff:192.168.1.10")).Should().BeTrue();
    }

    [Fact]
    public void SiteLocal_IsDefault()
    {
        ProxyDialPolicy.SiteLocal.IsDefault.Should().BeTrue();
        ProxyDialPolicy.SiteLocal.PinnedCount.Should().Be(0);
    }

    #endregion

    #region Pinned policy

    [Fact]
    public void Pinned_ReplacesDefaultEntirely()
    {
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { "10.20.30.0/24" }, out var error);

        error.Should().BeNull();
        policy.Should().NotBeNull();
        policy!.IsDefault.Should().BeFalse();
        policy.IsAllowed(IPAddress.Parse("10.20.30.1")).Should().BeTrue();
        policy.IsAllowed(IPAddress.Parse("10.20.30.200")).Should().BeTrue();
        // Site-local but outside the pin: denied (replace, not extend)
        policy.IsAllowed(IPAddress.Parse("10.10.2.1")).Should().BeFalse();
        policy.IsAllowed(IPAddress.Parse("192.168.1.1")).Should().BeFalse();
    }

    [Fact]
    public void Pinned_CanAdmitPublicTargets()
    {
        // The operator escape hatch for an exotic public-IP custom target
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { "192.168.77.0/24", "203.0.113.5" }, out _);

        policy!.IsAllowed(IPAddress.Parse("203.0.113.5")).Should().BeTrue();
        policy.IsAllowed(IPAddress.Parse("203.0.113.6")).Should().BeFalse();
        policy.IsAllowed(IPAddress.Parse("192.168.77.1")).Should().BeTrue();
    }

    [Fact]
    public void Pinned_BareIpsBecomeExactMatches()
    {
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { "10.20.30.1", "fd00::5" }, out _);

        policy!.PinnedCount.Should().Be(2);
        policy.IsAllowed(IPAddress.Parse("10.20.30.1")).Should().BeTrue();
        policy.IsAllowed(IPAddress.Parse("10.20.30.2")).Should().BeFalse();
        policy.IsAllowed(IPAddress.Parse("fd00::5")).Should().BeTrue();
        policy.IsAllowed(IPAddress.Parse("fd00::6")).Should().BeFalse();
    }

    [Fact]
    public void Pinned_UnwrapsIPv4MappedIPv6()
    {
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { "192.168.1.0/24" }, out _);

        policy!.IsAllowed(IPAddress.Parse("::ffff:192.168.1.10")).Should().BeTrue();
        policy.IsAllowed(IPAddress.Parse("::ffff:192.168.2.10")).Should().BeFalse();
    }

    [Fact]
    public void Pinned_SkipsBlankEntries()
    {
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { " 10.20.30.0/24 ", "", "  ", null }, out var error);

        error.Should().BeNull();
        policy!.PinnedCount.Should().Be(1);
        policy.IsAllowed(IPAddress.Parse("10.20.30.1")).Should().BeTrue();
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.20.30.0/33")]
    [InlineData("10.20.30.0/-1")]
    [InlineData("banana/24")]
    [InlineData("fd00::/129")]
    public void Pinned_InvalidEntryFailsLoudly(string entry)
    {
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { "10.20.30.0/24", entry }, out var error);

        policy.Should().BeNull();
        error.Should().NotBeNull();
        error.Should().Contain(entry);
    }

    [Fact]
    public void Pinned_EmptyListFailsLoudly()
    {
        var policy = ProxyDialPolicy.FromPinnedCidrs(new[] { "", "  " }, out var error);

        policy.Should().BeNull();
        error.Should().NotBeNull();
    }

    #endregion
}
