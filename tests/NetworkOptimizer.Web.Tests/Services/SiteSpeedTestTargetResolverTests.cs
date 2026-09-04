using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class SiteSpeedTestTargetResolverTests
{
    [Theory]
    [InlineData("speedtest.example.com", "speedtest.example.com", null)]
    [InlineData("192.0.2.10", "192.0.2.10", null)]
    [InlineData("speedtest.example.com:3000", "speedtest.example.com", 3000)]
    [InlineData("192.0.2.10:8443", "192.0.2.10", 8443)]
    [InlineData("2001:db8::10", "[2001:db8::10]", null)]
    [InlineData("[2001:db8::10]:3000", "[2001:db8::10]", 3000)]
    [InlineData("[2001:db8::10]", "[2001:db8::10]", null)]
    public void SplitHostAndPort_KeepsAnOperatorPortAndBracketsIpv6(string value, string host, int? port)
    {
        var (h, p) = SiteSpeedTestTargetResolver.SplitHostAndPort(value);
        h.Should().Be(host);
        p.Should().Be(port);
    }
}
