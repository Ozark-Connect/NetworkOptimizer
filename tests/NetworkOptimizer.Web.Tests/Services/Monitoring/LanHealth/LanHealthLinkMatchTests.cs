using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

public class LanHealthLinkMatchTests
{
    private const string Gw = "aa:bb:cc:00:00:01", Sw = "aa:bb:cc:00:00:02", Ap = "aa:bb:cc:00:00:03", Pc = "aa:bb:cc:00:00:04";

    private static LinkHealth Fabric(string up, string down) => new()
    {
        Link = new LinkRecord { Id = $"{up}|1-{down}|", Kind = LinkKind.FabricWired, Upstream = new LinkEnd { DeviceMac = up }, Downstream = new LinkEnd { DeviceMac = down } },
    };

    private static LinkHealth Access(string sw, int port, string client) => new()
    {
        Link = new LinkRecord { Id = $"{sw}|{port}-access", Kind = LinkKind.Access, ClientMac = client, Upstream = new LinkEnd { DeviceMac = sw, PortIdx = port }, Downstream = new LinkEnd() },
    };

    private static readonly LinkHealth[] Links = [Fabric(Gw, Sw), Fabric(Sw, Ap), Access(Sw, 7, Pc)];

    [Theory]
    [InlineData(Gw, Sw)]
    [InlineData(Sw, Gw)]
    [InlineData("AA-BB-CC-00-00-02", "aa:bb:cc:00:00:03")]
    public void Between_FabricLink_EitherWayAndAnyMacFormat(string a, string b) =>
        LanHealthLinkMatch.Between(Links, a, b).Should().NotBeNull().And.Match<LinkHealth>(l => l.Link.IsFabric);

    [Fact]
    public void Between_ClientAndItsSwitch_IsTheAccessPort()
    {
        LanHealthLinkMatch.Between(Links, Sw, Pc)!.Link.Id.Should().Be($"{Sw}|7-access");
        LanHealthLinkMatch.Between(Links, Pc, Sw)!.Link.Id.Should().Be($"{Sw}|7-access");
    }

    [Fact]
    public void Between_DevicesWithNoLink_IsNull()
    {
        LanHealthLinkMatch.Between(Links, Gw, Ap).Should().BeNull();
        LanHealthLinkMatch.Between(Links, Gw, Pc).Should().BeNull("the client's port is on the switch, not the gateway");
        LanHealthLinkMatch.Between(Links, "", Sw).Should().BeNull();
    }

    [Fact]
    public void ForClient_FindsItsAccessPort() =>
        LanHealthLinkMatch.ForClient(Links, Pc)!.Link.Id.Should().Be($"{Sw}|7-access");

    [Fact]
    public void SharedPort_EveryOccupantFindsIt()
    {
        const string host = "aa:bb:cc:00:00:10", vm = "aa:bb:cc:00:00:11";
        var shared = new LinkHealth
        {
            Link = new LinkRecord { Id = $"{Sw}|9-access", Kind = LinkKind.Access, ClientMac = host, OccupantMacs = [host, vm],
                Upstream = new LinkEnd { DeviceMac = Sw, PortIdx = 9 }, Downstream = new LinkEnd() },
        };
        LinkHealth[] links = [.. Links, shared];
        LanHealthLinkMatch.ForClient(links, vm)!.Link.Id.Should().Be($"{Sw}|9-access");
        LanHealthLinkMatch.Between(links, Sw, vm)!.Link.Id.Should().Be($"{Sw}|9-access");
    }
}
