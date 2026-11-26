using FluentAssertions;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The Port Statistics Client column takes a port's client, name and ip as LAN Health resolves them, so an asleep
/// or unlisted client names and links the same on both pages.
/// </summary>
public class PortStatsClientTests
{
    private const string Switch = "aa:bb:cc:00:00:01";
    private const string Tv = "aa:bb:cc:00:00:02";
    private const string Other = "aa:bb:cc:00:00:03";

    private static Dictionary<(string Mac, int Port), LinkHealth> Port(int port, LinkKind kind = LinkKind.Access,
        string? clientMac = Tv, string name = "Living Room TV", string? ip = "192.0.2.40", params string[] occupants) => new()
    {
        [(Switch, port)] = new LinkHealth
        {
            Link = new LinkRecord
            {
                Kind = kind,
                Upstream = new LinkEnd { DeviceMac = Switch, PortIdx = port },
                Downstream = new LinkEnd { DeviceMac = clientMac ?? "", DeviceName = name },
                ClientMac = clientMac,
                OccupantMacs = occupants,
                ClientIp = ip,
            },
        },
    };

    [Fact]
    public void AnAccessPortWithOneClientIsTheFallback()
    {
        PortStatsEndpoints.LanHealthPortClient(Port(2), Switch, 2)!.ClientMac.Should().Be(Tv);
    }

    [Fact]
    public void ASharedPortNamesNoOne()
    {
        PortStatsEndpoints.LanHealthPortClient(Port(10, occupants: [Tv, Other]), Switch, 10).Should().BeNull();
    }

    [Fact]
    public void AFabricLinkIsNoClient()
    {
        PortStatsEndpoints.LanHealthPortClient(Port(9, LinkKind.FabricWired), Switch, 9).Should().BeNull();
    }

    [Fact]
    public void ASwitchToSwitchPortIsFabricAndAnAccessPortIsNot()
    {
        // The uplink once carried a client for a minute (a recabling): it still names no one.
        PortStatsEndpoints.IsFabricPort(Port(9, LinkKind.FabricWired), Switch, 9).Should().BeTrue();
        PortStatsEndpoints.IsFabricPort(Port(2), Switch, 2).Should().BeFalse();
        PortStatsEndpoints.IsFabricPort(Port(2), Switch, 3).Should().BeFalse();
    }

    [Fact]
    public void APortWithNoClientMacIsNoClient()
    {
        PortStatsEndpoints.LanHealthPortClient(Port(4, clientMac: null), Switch, 4).Should().BeNull();
    }

    [Fact]
    public void AListedClientWithNoIpTakesLanHealthsAddress()
    {
        var lan = PortStatsEndpoints.LanHealthPortClient(Port(6), Switch, 6);
        PortStatsEndpoints.FillClient(lan, null, Tv, "", "Living Room TV")
            .Should().Be((Tv, "192.0.2.40", "Living Room TV"));
    }

    [Fact]
    public void ItsOwnNameAndAddressWin()
    {
        var lan = PortStatsEndpoints.LanHealthPortClient(Port(6), Switch, 6);
        PortStatsEndpoints.FillClient(lan, null, Tv, "192.0.2.41", "TV")
            .Should().Be((Tv, "192.0.2.41", "TV"));
    }

    [Fact]
    public void LanHealthNeverNamesADifferentClient()
    {
        // A historic scrub's past occupant: today's LAN Health client on the port is someone else.
        var lan = PortStatsEndpoints.LanHealthPortClient(Port(6), Switch, 6);
        PortStatsEndpoints.FillClient(lan, new Dictionary<string, string> { [Other] = "Old PC" }, Other, null, null)
            .Should().Be((Other, (string?)null, "Old PC"));
    }

    [Fact]
    public void LanHealthsMacFallbackIsNoName()
    {
        var lan = PortStatsEndpoints.LanHealthPortClient(Port(5, name: Tv), Switch, 5);
        PortStatsEndpoints.FillClient(lan, new Dictionary<string, string> { [Tv] = "Kira Rig" }, Tv, null, null)
            .Should().Be((Tv, "192.0.2.40", "Kira Rig"));
    }

    [Fact]
    public void NothingKnownLeavesTheMacForTheTable()
    {
        PortStatsEndpoints.FillClient(null, null, Tv, " ", " ").Should().Be((Tv, (string?)null, (string?)null));
    }
}
