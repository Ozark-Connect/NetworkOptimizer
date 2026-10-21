using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

public class WellKnownPortsTests
{
    [Fact]
    public void EachPortOnce() =>
        NetworkUtilities.WellKnownPorts.Select(p => p.Port).Should().OnlyHaveUniqueItems();

    [Theory]
    [InlineData(53, "DNS")]
    [InlineData(443, "HTTPS")]
    [InlineData(11211, "Memcached")]
    [InlineData(27017, "MongoDB")]
    [InlineData(1900, "SSDP")]
    public void NamesAPort(int port, string name) => NetworkUtilities.GetPortServiceName(port).Should().Be(name);

    [Fact]
    public void UnknownPortHasNoName() => NetworkUtilities.GetPortServiceName(47000).Should().BeNull();

    [Fact]
    public void AmplificationPorts_FitOneMultiportMatch()
    {
        // WAN Flood Protection puts them in one iptables multiport match, which takes 15 ports at most.
        NetworkUtilities.AmplificationPorts.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(15);
        NetworkUtilities.AmplificationPorts.Should().Contain([53, 123, 1900, 11211]);
    }

    [Fact]
    public void AmplificationPorts_LeaveRealInboundAlone() =>
        // Real inbound connections come from these: BitTorrent DHT, Steam queries, and HTTPS/QUIC.
        NetworkUtilities.AmplificationPorts.Should().NotContain([6881, 27015, 443]);
}
