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

    // --- MLO: an MLO backhaul is one downlink entry PER LINK, all sharing the child's base MAC ---

    private static DownlinkTableEntry MloLink(string radio, long txRate, long rxRate, int? signal = null) => new()
    {
        Mac = $"vwire-{radio}",
        SerialNo = ChildMac,
        MldMac = ChildMac,
        IsMlo = true,
        Radio = radio,
        Signal = signal,
        TxRate = txRate,
        RxRate = rxRate,
    };

    [Fact]
    public void BuildMeshParentByChild_MloLinks_AggregateIntoOneClaim()
    {
        // STR runs the links concurrently, so the pair's capacity is the sum, and each link
        // is kept for per-band display.
        var parent = Device(ParentMac);
        parent.DownlinkTable = [MloLink("6e", 2_594_200, 2_161_800, -62), MloLink("na", 2_161_800, 1_201_000, -50)];

        var claim = UniFiDiscovery.BuildMeshParentByChild([parent, Device(ChildMac)])[ChildMac];

        claim.ParentMac.Should().Be(ParentMac);
        claim.IsMlo.Should().BeTrue();
        claim.TxRateKbps.Should().Be(2_594_200 + 2_161_800);
        claim.RxRateKbps.Should().Be(2_161_800 + 1_201_000);
        claim.Links.Should().HaveCount(2);
        claim.Links.Select(l => l.Radio).Should().BeEquivalentTo(["6e", "na"]);
    }

    [Fact]
    public void BuildMeshParentByChild_MloEntriesOutrankAnUnflaggedRowForTheSameChild()
    {
        // A firmware that keeps a legacy combined row next to the per-link entries must not be
        // double-counted: only is_mlo-flagged entries are ever aggregated.
        var parent = Device(ParentMac);
        parent.DownlinkTable =
        [
            new DownlinkTableEntry { Mac = "vwire-combined", SerialNo = ChildMac, TxRate = 4_756_000, RxRate = 3_362_800 },
            MloLink("6e", 2_594_200, 2_161_800),
            MloLink("na", 2_161_800, 1_201_000),
        ];

        var claim = UniFiDiscovery.BuildMeshParentByChild([parent, Device(ChildMac)])[ChildMac];

        claim.TxRateKbps.Should().Be(2_594_200 + 2_161_800);
        claim.Links.Should().HaveCount(2);
    }

    [Fact]
    public void BuildMeshParentByChild_DuplicateUnflaggedEntries_KeepLastEntryWins()
    {
        // Pre-MLO behavior, preserved exactly: duplicate non-MLO listings are never summed.
        var parent = Device(ParentMac);
        parent.DownlinkTable =
        [
            new DownlinkTableEntry { Mac = "vwire-stale", SerialNo = ChildMac, TxRate = 866_000, RxRate = 585_000 },
            new DownlinkTableEntry { Mac = "vwire-fresh", SerialNo = ChildMac, TxRate = 1_201_000, RxRate = 866_000 },
        ];

        var claim = UniFiDiscovery.BuildMeshParentByChild([parent, Device(ChildMac)])[ChildMac];

        claim.IsMlo.Should().BeFalse();
        claim.TxRateKbps.Should().Be(1_201_000);
        claim.RxRateKbps.Should().Be(866_000);
        claim.Links.Should().HaveCount(1);
    }

    [Fact]
    public void BuildMeshParentByChild_FallsBackToMldMacWhenSerialNoIsAbsent()
    {
        var parent = Device(ParentMac);
        parent.DownlinkTable = [new DownlinkTableEntry { Mac = "vwire-bssid", MldMac = ChildMac, IsMlo = true, TxRate = 866_000 }];

        UniFiDiscovery.BuildMeshParentByChild([parent, Device(ChildMac)]).Should().ContainKey(ChildMac);
    }

    [Fact]
    public void MeshParentClaim_DefaultInstance_HasEmptyLinksNotNull()
    {
        default(UniFiDiscovery.MeshParentClaim).Links.Should().NotBeNull().And.BeEmpty();
    }
}
