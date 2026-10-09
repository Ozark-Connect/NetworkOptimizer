using FluentAssertions;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class AlertSourceDescriberTests
{
    private static ThreatEvent Flow(string sourceIp, string? network, DateTime time) => new()
    {
        SourceIp = sourceIp,
        NetworkName = network,
        Timestamp = time,
        EventSource = EventSource.TrafficFlow
    };

    [Fact]
    public async Task PublicSource_UsesCountryAndAsn()
    {
        var describer = new AlertSourceDescriber([], () => null);

        var source = await describer.DescribeAsync("203.0.113.5", "US", "Example Transit", default);

        source.Text.Should().Be("203.0.113.5 (US)");
        source.InternalSuffix.Should().BeEmpty();
        var context = source.WithContext(new Dictionary<string, string>());
        context["country"].Should().Be("US");
        context["asn"].Should().Be("Example Transit");
        context.Should().NotContainKey("network");
    }

    [Fact]
    public async Task InternalSource_IgnoresCountryAndUsesNetwork()
    {
        var describer = new AlertSourceDescriber(
            [Flow("192.168.10.20", "Main LAN", DateTime.UtcNow)], () => null);

        // Pre-fix rows carried the destination's country on a private source; it must not show.
        var source = await describer.DescribeAsync("192.168.10.20", "US", "Some CDN", default);

        source.Text.Should().Be("192.168.10.20 (Main LAN)");
        source.InternalSuffix.Should().Be(" (Main LAN)");
        var context = source.WithContext(new Dictionary<string, string>());
        context.Should().NotContainKey("country");
        context.Should().NotContainKey("asn");
        context["network"].Should().Be("Main LAN");
    }

    [Fact]
    public async Task InternalSource_LatestNetworkWins()
    {
        var now = DateTime.UtcNow;
        var describer = new AlertSourceDescriber(
            [Flow("10.0.0.5", "New LAN", now), Flow("10.0.0.5", "Old LAN", now.AddHours(-1))], () => null);

        var source = await describer.DescribeAsync("10.0.0.5", null, null, default);

        source.Text.Should().Be("10.0.0.5 (New LAN)");
    }

    [Fact]
    public async Task InternalSource_NothingKnown_SaysInternal()
    {
        var describer = new AlertSourceDescriber([], () => null);

        var source = await describer.DescribeAsync("10.0.0.9", null, null, default);

        source.Text.Should().Be("10.0.0.9 (internal)");
    }
}
