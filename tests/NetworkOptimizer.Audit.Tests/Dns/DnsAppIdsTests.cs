using FluentAssertions;
using NetworkOptimizer.Audit.Dns;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class DnsAppIdsTests
{
    [Fact]
    public void AllDnsAppIds_ContainsExpectedIds()
    {
        DnsAppIds.AllDnsAppIds.Should().Contain(DnsAppIds.Dns);
        DnsAppIds.AllDnsAppIds.Should().Contain(DnsAppIds.DnsOverTls);
        DnsAppIds.AllDnsAppIds.Should().Contain(DnsAppIds.DnsOverHttps);
        DnsAppIds.AllDnsAppIds.Should().HaveCount(3);
    }

    [Fact]
    public void Dns_HasCorrectValue()
    {
        DnsAppIds.Dns.Should().Be(589885);
    }

    [Fact]
    public void DnsOverTls_HasCorrectValue()
    {
        DnsAppIds.DnsOverTls.Should().Be(1310917);
    }

    [Fact]
    public void DnsOverHttps_HasCorrectValue()
    {
        DnsAppIds.DnsOverHttps.Should().Be(1310919);
    }

    [Theory]
    [InlineData(589885)]   // DNS
    [InlineData(1310917)]  // DoT
    [InlineData(1310919)]  // DoH
    public void IsDnsApp_WithValidDnsAppId_ReturnsTrue(int appId)
    {
        DnsAppIds.IsDnsApp(appId).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12345)]
    [InlineData(-1)]
    public void IsDnsApp_WithInvalidAppId_ReturnsFalse(int appId)
    {
        DnsAppIds.IsDnsApp(appId).Should().BeFalse();
    }

    [Fact]
    public void IsDns53App_WithDnsAppId_ReturnsTrue()
    {
        DnsAppIds.IsDns53App(DnsAppIds.Dns).Should().BeTrue();
    }

    [Fact]
    public void IsDns53App_WithOtherDnsAppId_ReturnsFalse()
    {
        DnsAppIds.IsDns53App(DnsAppIds.DnsOverTls).Should().BeFalse();
        DnsAppIds.IsDns53App(DnsAppIds.DnsOverHttps).Should().BeFalse();
    }

    [Fact]
    public void IsPort853App_WithDotAppId_ReturnsTrue()
    {
        DnsAppIds.IsPort853App(DnsAppIds.DnsOverTls).Should().BeTrue();
    }

    [Fact]
    public void IsPort853App_WithOtherAppId_ReturnsFalse()
    {
        DnsAppIds.IsPort853App(DnsAppIds.Dns).Should().BeFalse();
        DnsAppIds.IsPort853App(DnsAppIds.DnsOverHttps).Should().BeFalse();
    }

    [Fact]
    public void IsPort443App_WithDohAppId_ReturnsTrue()
    {
        DnsAppIds.IsPort443App(DnsAppIds.DnsOverHttps).Should().BeTrue();
    }

    [Fact]
    public void IsPort443App_WithOtherAppId_ReturnsFalse()
    {
        DnsAppIds.IsPort443App(DnsAppIds.Dns).Should().BeFalse();
        DnsAppIds.IsPort443App(DnsAppIds.DnsOverTls).Should().BeFalse();
    }
}
