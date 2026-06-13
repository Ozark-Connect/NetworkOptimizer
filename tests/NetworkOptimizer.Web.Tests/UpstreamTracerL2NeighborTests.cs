using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Pins the WAN L2 neighbor selection so a bridged CPE's LAN-side address can never
/// again be proposed as an ISP hop. Modeled on a real regression: an ISP gateway in
/// passthrough listed its private LAN IP (STALE, first line) and its carrier-side
/// public IP (REACHABLE) under the same MAC, and the parser took the first line.
/// </summary>
public class UpstreamTracerL2NeighborTests
{
    [Fact]
    public void Prefers_public_reachable_over_private_stale_with_same_mac()
    {
        const string output = """
            1.1.1.1  FAILED
            192.168.1.254 lladdr aa:bb:cc:dd:ee:06 STALE
            203.0.113.1 lladdr aa:bb:cc:dd:ee:06 REACHABLE
            fe80::a8bb:ccff:fedd:ee06 lladdr aa:bb:cc:dd:ee:06 router STALE
            """;

        var selected = UpstreamTracerService.SelectWanNeighbor(output);

        selected.Should().NotBeNull();
        selected!.Value.Ip.Should().Be("203.0.113.1");
        selected.Value.Mac.Should().Be("aa:bb:cc:dd:ee:06");
    }

    [Fact]
    public void Prefers_public_even_when_stale_over_private_reachable()
    {
        const string output = """
            192.168.1.254 lladdr aa:bb:cc:dd:ee:06 REACHABLE
            203.0.113.1 lladdr aa:bb:cc:dd:ee:06 STALE
            """;

        UpstreamTracerService.SelectWanNeighbor(output)!.Value.Ip.Should().Be("203.0.113.1");
    }

    [Fact]
    public void Prefers_cgnat_over_private()
    {
        const string output = """
            192.168.1.254 lladdr aa:bb:cc:dd:ee:06 REACHABLE
            100.64.12.1 lladdr aa:bb:cc:dd:ee:06 REACHABLE
            """;

        UpstreamTracerService.SelectWanNeighbor(output)!.Value.Ip.Should().Be("100.64.12.1");
    }

    [Fact]
    public void Prefers_reachable_over_stale_within_same_class()
    {
        const string output = """
            203.0.113.7 lladdr aa:bb:cc:dd:ee:01 STALE
            203.0.113.1 lladdr aa:bb:cc:dd:ee:06 REACHABLE
            """;

        UpstreamTracerService.SelectWanNeighbor(output)!.Value.Ip.Should().Be("203.0.113.1");
    }

    [Fact]
    public void Falls_back_to_private_neighbor_when_nothing_else_exists()
    {
        // The MAC still matters for OUI labeling even when the only entry is the
        // CPE's LAN side; the injection guard keeps it out of the hop proposals
        const string output = "192.168.1.254 lladdr aa:bb:cc:dd:ee:06 STALE";

        var selected = UpstreamTracerService.SelectWanNeighbor(output);

        selected.Should().NotBeNull();
        selected!.Value.Ip.Should().Be("192.168.1.254");
    }

    [Fact]
    public void Ignores_failed_and_ipv6_only_output()
    {
        const string output = """
            1.1.1.1  FAILED
            fe80::a8bb:ccff:fedd:ee06 lladdr aa:bb:cc:dd:ee:06 router STALE
            """;

        UpstreamTracerService.SelectWanNeighbor(output).Should().BeNull();
    }

    [Fact]
    public void Empty_output_returns_null()
    {
        UpstreamTracerService.SelectWanNeighbor(null).Should().BeNull();
        UpstreamTracerService.SelectWanNeighbor("").Should().BeNull();
    }

    [Theory]
    [InlineData("192.168.1.254", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.12.1", true)]
    [InlineData("203.0.113.1", true)]
    [InlineData(null, false)]
    public void Only_carrier_side_addresses_are_injectable_as_access_hops(string? ip, bool expected)
    {
        UpstreamTracerService.IsInjectableAccessHopAddress(ip).Should().Be(expected);
    }
}
