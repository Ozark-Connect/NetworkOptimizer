using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>Client Performance links name the Client by MAC, with its IP as the fallback.</summary>
public class ClientPerformanceLinkTests
{
    [Fact]
    public void ALinkCarriesBothTheMacAndTheIp() =>
        ClientPerformanceLink.Href("AA-BB-CC-00-00-01", "192.0.2.10", "tab=speed&range=30d")
            .Should().Be("/client-dashboard?mac=aa%3Abb%3Acc%3A00%3A00%3A01&ip=192.0.2.10&tab=speed&range=30d");

    [Fact]
    public void AnIpAloneStaysAnIpLink() =>
        ClientPerformanceLink.Href(null, "192.0.2.10").Should().Be("/client-dashboard?ip=192.0.2.10");

    [Fact]
    public void SomethingThatIsNotAMacIsLeftOut() =>
        ClientPerformanceLink.Href("not-a-mac", "192.0.2.10").Should().Be("/client-dashboard?ip=192.0.2.10");

    [Fact]
    public void NeitherIsNoLink() =>
        ClientPerformanceLink.Href(null, " ").Should().BeNull();
}
