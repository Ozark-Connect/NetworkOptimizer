using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class DestinationCardTests
{
    [Fact]
    public void PublicAddress_GetsAddressNameAndThreatsLink()
    {
        var card = LiveTrafficLinks.DestinationCard("203.0.113.7", "Example Resolver");

        card.Should().NotBeNull();
        card!["data-dest-ip"].Should().Be("203.0.113.7");
        card["data-dest-name"].Should().Be("Example Resolver");
        card["data-dest-link"].Should().Be("/threats?tab=drilldown&ip=203.0.113.7");
    }

    [Fact]
    public void ThreatsLinkOff_LeavesItOut()
    {
        var card = LiveTrafficLinks.DestinationCard("203.0.113.7", threatsLink: false);

        card.Should().NotBeNull();
        card!.Should().NotContainKey("data-dest-link");
    }

    [Theory]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.5")]
    [InlineData("fd00::1")]
    [InlineData("")]
    [InlineData(null)]
    public void PrivateOrMissingAddress_GetsNoCard(string? address)
    {
        LiveTrafficLinks.DestinationCard(address).Should().BeNull();
    }

    [Fact]
    public void Hostname_GetsTheCardWithoutAThreatsLink()
    {
        var card = LiveTrafficLinks.DestinationCard("dns.example.com");

        card.Should().NotBeNull();
        card!["data-dest-ip"].Should().Be("dns.example.com");
        card.Should().NotContainKey("data-dest-link");
    }

    [Theory]
    [InlineData("Example Networks LLC")]
    [InlineData("localhost")]
    [InlineData("-")]
    public void NotAHostName_GetsNoCard(string text)
    {
        LiveTrafficLinks.DestinationCard(text).Should().BeNull();
    }

    [Fact]
    public void NameEqualToAddress_IsNotRepeated()
    {
        var card = LiveTrafficLinks.DestinationCard("203.0.113.7", "203.0.113.7");

        card!.Should().NotContainKey("data-dest-name");
    }
}
