using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class UpstreamTracerLocalSideTests
{
    private static readonly string[] Gateway = ["192.168.1.1", "203.0.113.20"];

    [Fact]
    public void ContainerBridgeBeforeTheGateway_IsOurSide()
    {
        // A bridge-networked container: hop 1 is the Docker bridge, hop 2 our gateway.
        var trace = new List<(int, string)> { (1, "172.18.0.1"), (2, "192.168.1.1"), (3, "100.64.0.1"), (4, "10.0.5.1") };

        UpstreamTracerService.HopsBeforeGateway([trace], Gateway)
            .Should().BeEquivalentTo(["172.18.0.1"]);
    }

    [Fact]
    public void HopsAfterTheGateway_AreNeverOurSide()
    {
        var trace = new List<(int, string)> { (1, "192.168.1.1"), (2, "10.0.0.1"), (3, "198.51.100.1") };

        UpstreamTracerService.HopsBeforeGateway([trace], Gateway).Should().BeEmpty();
    }

    [Fact]
    public void ATraceWhereTheGatewayNeverAnswered_ContributesNothing()
    {
        // A probe running on the gateway itself, or a gateway that drops TTL-exceeded: no position to judge from.
        var trace = new List<(int, string)> { (1, "10.0.0.1"), (2, "198.51.100.1") };

        UpstreamTracerService.HopsBeforeGateway([trace], Gateway).Should().BeEmpty();
    }

    [Fact]
    public void ALanRouterAndABridge_BothBeforeTheGateway_AreOurSide()
    {
        var withBridge = new List<(int, string)> { (1, "172.17.0.1"), (2, "192.168.20.1"), (3, "192.168.1.1"), (4, "100.64.0.1") };
        var other = new List<(int, string)> { (1, "172.17.0.1"), (2, "192.168.20.1"), (4, "100.64.0.1") };

        UpstreamTracerService.HopsBeforeGateway([withBridge, other], Gateway)
            .Should().BeEquivalentTo(["172.17.0.1", "192.168.20.1"]);
    }

    [Fact]
    public void NoAccessAsn_AllLookupsFailed_StopsAtTheFirstPublicHop()
    {
        // Every hop unattributed: the old TakeWhile(Asn == null) made the whole path, destinations included, access hops.
        var hops = new List<(string, bool)>
        {
            ("100.64.0.1", false), ("10.0.5.1", false), ("10.0.6.1", false),
            ("198.51.100.1", false), ("203.0.113.53", false), ("198.51.100.200", false)
        };

        UpstreamTracerService.LeadingUnattributedAccessHops(hops, ["203.0.113.53"])
            .Should().Equal("100.64.0.1", "10.0.5.1", "10.0.6.1");
    }

    [Fact]
    public void NoAccessAsn_ATraceEndpoint_IsNeverAnAccessHop()
    {
        var hops = new List<(string, bool)> { ("100.64.0.1", false), ("100.100.100.100", false) };

        UpstreamTracerService.LeadingUnattributedAccessHops(hops, ["100.100.100.100"])
            .Should().Equal("100.64.0.1");
    }
}
