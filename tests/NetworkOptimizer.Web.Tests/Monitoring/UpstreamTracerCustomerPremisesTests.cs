using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class UpstreamTracerCustomerPremisesTests
{
    private const double GponFloor = 2.0;
    private static readonly string[] Gateway = ["192.168.1.1"];

    private static HashSet<string> Judge(params List<(string, double?)>[] traces) =>
        UpstreamTracerService.CustomerPremisesHops(traces, Gateway, GponFloor);

    [Fact]
    public void APassthroughGateway_TwiceAsFastAsTheNextHop_IsCustomerPremises()
    {
        // GPON adds only 0.6 ms: the passthrough gateway at 0.6 ms, the first ISP hop at 1.2 ms.
        Judge([("192.168.1.1", 0.2), ("192.168.20.254", 0.6), ("198.51.100.1", 1.2), ("198.51.100.9", 1.4)])
            .Should().Contain("192.168.20.254");
    }

    [Fact]
    public void ProbedFromTheGatewayItself_TheFirstHopIsJudged()
    {
        Judge([("192.168.20.254", 0.6), ("198.51.100.1", 1.2)]).Should().Contain("192.168.20.254");
    }

    [Fact]
    public void APrivateHopPastTheFirstIspHop_IsNeverJudged()
    {
        // ISP gear numbered in 10/8 one hop beyond the access router, answering fast from its data plane.
        Judge([("192.168.1.1", 0.2), ("198.51.100.1", 1.2), ("10.0.5.1", 0.8), ("10.0.5.2", 2.0)])
            .Should().BeEmpty();
    }

    [Fact]
    public void ISPGearOnPrivateAddresses_BehindACgnatFirstHop_IsKept()
    {
        Judge([("192.168.1.1", 0.2), ("100.64.0.1", 2.33), ("10.0.5.1", 2.23), ("10.0.5.2", 2.60), ("198.51.100.1", 3.7)])
            .Should().BeEmpty();
    }

    [Fact]
    public void AnIspAssignedPrivateFirstHop_WithANearNextHop_IsKept()
    {
        // 1.4 to 1.7 ms is a hop inside the ISP, not an access-link crossing.
        Judge([("192.168.1.1", 0.2), ("10.0.5.1", 1.4), ("10.0.5.2", 1.7)]).Should().BeEmpty();
    }

    [Fact]
    public void AnInBuildingIspEdge_SubMsApart_IsKept()
    {
        // 0.4 to 0.6 ms is 1.5x but only 0.2 ms: noise, not an access-link crossing.
        Judge([("192.168.1.1", 0.1), ("10.0.5.1", 0.4), ("198.51.100.1", 0.6)]).Should().BeEmpty();
    }

    [Fact]
    public void AnIPv6FirstHop_IsNeverJudged()
    {
        Judge([("2001:db8::1", 0.6), ("2001:db8:1::1", 1.5)]).Should().BeEmpty();
    }

    [Fact]
    public void AFirstHopAtOrOverTheFloor_IsKept()
    {
        Judge([("192.168.1.1", 0.2), ("10.0.5.1", 2.1), ("198.51.100.1", 4.5)]).Should().BeEmpty();
    }

    [Fact]
    public void APrivateCmtsFirstHop_UnderTheDocsisFloor_IsKept()
    {
        // DOCSIS floor is 9 ms: a CMTS at 7 ms with its next hop at 11 ms is ISP gear, not CPE.
        UpstreamTracerService.CustomerPremisesHops(
                [[("192.168.1.1", 0.2), ("10.20.0.1", 7.0), ("198.51.100.1", 11.0)]], Gateway, 9.0)
            .Should().BeEmpty();
    }

    [Fact]
    public void CpeOnASlowMedium_IsStillCustomerPremises()
    {
        UpstreamTracerService.CustomerPremisesHops(
                [[("192.168.1.1", 0.2), ("192.168.20.254", 0.6), ("10.20.0.1", 8.0)]], Gateway, 9.0)
            .Should().Contain("192.168.20.254");
    }

    [Fact]
    public void JudgedOnTheTraceWhereTheHopAnsweredFastest()
    {
        // Under a busy sweep one trace read the box at 2.4 ms; the cleanest read is what counts.
        Judge([("192.168.20.254", 2.4), ("198.51.100.1", 3.0)], [("192.168.20.254", 0.9), ("198.51.100.1", 2.0)])
            .Should().Contain("192.168.20.254");
    }

    [Fact]
    public void PublicAndCgnatFirstHops_AreNeverJudged()
    {
        Judge([("100.64.0.1", 0.5), ("203.0.113.1", 3.0)], [("198.51.100.1", 0.6), ("203.0.113.1", 3.0)])
            .Should().BeEmpty();
    }

    [Fact]
    public void PrivateFirstHops_AreTakenByPositionWithTheirNextHops()
    {
        var traces = new List<IReadOnlyList<string>>
        {
            new List<string> { "192.168.1.1", "192.168.20.254", "198.51.100.1", "10.0.5.1" },
            new List<string> { "192.168.1.1", "192.168.20.254", "198.51.100.2" },
            new List<string> { "192.168.1.1", "100.64.0.1", "10.0.5.2" },
            new List<string> { "10.0.9.1", "198.51.100.1" },
        };

        var result = UpstreamTracerService.PrivateFirstHopsPastGateway(traces, Gateway);

        result.Keys.Should().BeEquivalentTo(["192.168.20.254", "10.0.9.1"]);
        result["192.168.20.254"].Should().BeEquivalentTo(["198.51.100.1", "198.51.100.2"]);
    }

    private static HashSet<string> ByPing(string hop, string next, string? wanGateway, params (string, double)[] floors) =>
        UpstreamTracerService.CustomerPremisesByPing(
            new Dictionary<string, HashSet<string>> { [hop] = [next] },
            floors.ToDictionary(f => f.Item1, f => f.Item2), wanGateway);

    [Fact]
    public void Passthrough_UnderTheWanGatewaysFloor_IsCustomerPremises()
    {
        // Measured on a passthrough carrier gateway: 0.6 ms floor, the WAN default gateway 1.0 ms
        // (with 0.3 ms jitter, which must not matter).
        ByPing("192.168.1.254", "198.51.100.1", "203.0.113.1",
                ("192.168.1.254", 0.6), ("203.0.113.1", 1.0), ("198.51.100.1", 1.1))
            .Should().Contain("192.168.1.254");
    }

    [Fact]
    public void ASecondaryAddressOnTheIspRouter_AtTheGatewaysFloor_IsKept()
    {
        // The gateway anchor decides: the next hop being far slower does not override it.
        ByPing("10.0.5.1", "198.51.100.1", "203.0.113.1",
                ("10.0.5.1", 1.3), ("203.0.113.1", 1.4), ("198.51.100.1", 9.0))
            .Should().BeEmpty();
    }

    [Fact]
    public void ALeadUnder1_5x_IsKept()
    {
        // 0.9 vs 1.2 ms: 1.33x, too close to say the hop is in front of the access link.
        ByPing("10.0.5.1", "198.51.100.1", "203.0.113.1",
                ("10.0.5.1", 0.9), ("203.0.113.1", 1.2), ("198.51.100.1", 1.5))
            .Should().BeEmpty();
    }

    [Fact]
    public void IspGatewayInRouterMode_FarUnderTheNextHop_IsCustomerPremises()
    {
        // The ISP gateway is our WAN default gateway: 0.5 ms floor, the ISP's first router 1.3 ms.
        ByPing("192.168.1.254", "198.51.100.1", "192.168.1.254",
                ("192.168.1.254", 0.5), ("198.51.100.1", 1.3))
            .Should().Contain("192.168.1.254");
    }

    [Fact]
    public void IspAssignedPrivateWan_WithTheNextHopCloseBehind_IsKept()
    {
        // The ISP's router hands out the private WAN address: 1.2 ms floor, next hop 1.6 ms.
        ByPing("10.50.0.1", "198.51.100.1", "10.50.0.1",
                ("10.50.0.1", 1.2), ("198.51.100.1", 1.6))
            .Should().BeEmpty();
    }

    [Fact]
    public void NoGatewayFloor_FallsBackToTheNextHop()
    {
        ByPing("192.168.1.254", "198.51.100.1", "203.0.113.1",
                ("192.168.1.254", 0.5), ("198.51.100.1", 1.3))
            .Should().Contain("192.168.1.254");
    }

    [Fact]
    public void NoFloorForTheHop_JudgesNothing()
    {
        ByPing("192.168.1.254", "198.51.100.1", "203.0.113.1", ("203.0.113.1", 1.2), ("198.51.100.1", 1.3))
            .Should().BeEmpty();
    }

    [Fact]
    public void AHopWithNoTimedNextHop_IsNeverJudged()
    {
        Judge([("192.168.20.254", 0.9), ("198.51.100.1", null)]).Should().BeEmpty();
    }
}
