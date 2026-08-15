using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// UniFi describes a mesh pair from both ends and either end can go missing - a parent that has
/// just rebooted lists the child while the child's own uplink block stays empty. Everything that
/// keys off the child alone then loses the device: it vanishes from the 2D map, draws isolated on
/// the 3D one, and its mesh actions become unreachable.
/// </summary>
public class MeshParentDerivationTests
{
    private const string ParentMac = "aa:bb:cc:dd:ee:01";
    private const string ChildMac = "aa:bb:cc:dd:ee:02";

    private static DiscoveredDevice Device(string mac, params string[] downlinkSerials) => new()
    {
        Mac = mac,
        Name = "AP",
        DownlinkTable = downlinkSerials.Length == 0
            ? null
            : downlinkSerials.Select(s => new DownlinkTableEntry { Mac = "vwire-bssid", SerialNo = s }).ToList(),
    };

    [Fact]
    public void BuildMeshParentByChild_TakesTheChildFromTheParentsTable()
    {
        var map = UniFiDiscovery.BuildMeshParentByChild([Device(ParentMac, ChildMac), Device(ChildMac)]);

        map.Should().ContainKey(ChildMac).WhoseValue.ParentMac.Should().Be(ParentMac);
    }

    [Fact]
    public void BuildMeshParentByChild_CarriesTheParentsRatesAsTheParentReportedThem()
    {
        // Direction is the whole point of keeping them: the parent transmitting IS the child
        // receiving, so these are the inverse of the child's own uplink fields.
        var parent = Device(ParentMac, ChildMac);
        parent.DownlinkTable![0].TxRate = 866_000;
        parent.DownlinkTable![0].RxRate = 585_000;

        var claim = UniFiDiscovery.BuildMeshParentByChild([parent, Device(ChildMac)])[ChildMac];

        claim.TxRateKbps.Should().Be(866_000);
        claim.RxRateKbps.Should().Be(585_000);
    }

    [Fact]
    public void BuildMeshParentByChild_IgnoresChildrenThatAreNotDevicesOfOurs()
    {
        // serialno on a downlink entry is the child's base MAC, but the table also carries
        // entries for things that are not managed devices. Only known devices become edges.
        var map = UniFiDiscovery.BuildMeshParentByChild([Device(ParentMac, "ff:ff:ff:ff:ff:ff")]);

        map.Should().BeEmpty();
    }

    [Fact]
    public void BuildMeshParentByChild_WithNoDownlinkTables_IsEmpty()
    {
        var map = UniFiDiscovery.BuildMeshParentByChild([Device(ParentMac), Device(ChildMac)]);

        map.Should().BeEmpty();
    }

    [Fact]
    public void BuildMeshParentByChild_IsCaseInsensitiveOnMacs()
    {
        var map = UniFiDiscovery.BuildMeshParentByChild(
            [Device(ParentMac.ToUpperInvariant(), ChildMac.ToUpperInvariant()), Device(ChildMac)]);

        map.Should().ContainKey(ChildMac);
    }

    [Fact]
    public void BuildMeshParentByChild_RawDeviceOverload_MatchesDiscoveredDeviceDerivation()
    {
        // The fabric aggregate writer holds UniFiDeviceResponse, not DiscoveredDevice;
        // both overloads must produce the same claims.
        UniFiDeviceResponse Raw(string mac, params string[] serials) => new()
        {
            Mac = mac,
            DownlinkTable = serials.Length == 0
                ? null
                : serials.Select(s => new DownlinkTableEntry { Mac = "vwire-bssid", SerialNo = s, TxRate = 866_000, RxRate = 585_000 }).ToList(),
        };

        var map = UniFiDiscovery.BuildMeshParentByChild(new[] { Raw(ParentMac, ChildMac), Raw(ChildMac) });

        var claim = map.Should().ContainKey(ChildMac).WhoseValue;
        claim.ParentMac.Should().Be(ParentMac);
        claim.TxRateKbps.Should().Be(866_000);
        claim.RxRateKbps.Should().Be(585_000);
    }

    // Contradicts() decides whether the claim may be absorbed at all: only a child whose own
    // uplink is missing or names something else is derived. An agreeing child keeps its own
    // report, so the derived path is inert on healthy mesh pairs.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("aa:bb:cc:dd:ee:99", true)]
    [InlineData(ParentMac, false)]
    [InlineData("AA:BB:CC:DD:EE:01", false)]
    [InlineData("aa-bb-cc-dd-ee-01", false)]
    public void MeshParentClaim_Contradicts_OnlyWhenTheChildNamesSomethingElse(string? reported, bool expected)
    {
        var claim = new UniFiDiscovery.MeshParentClaim(ParentMac, 0, 0);

        claim.Contradicts(reported).Should().Be(expected);
    }
}
