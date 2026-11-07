using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class SiteBssidLookupTests
{
    private static AccessPointSnapshot Ap(string mac, string name, params (string Bssid, string Essid)[] vaps) => new()
    {
        Mac = mac,
        Name = name,
        Vaps = vaps.Select(v => new VapSnapshot { Bssid = v.Bssid, Essid = v.Essid }).ToList()
    };

    [Fact]
    public void Build_maps_every_vap_bssid_to_its_ap_case_insensitively()
    {
        var lookup = SiteBssidLookup.Build(new[]
        {
            Ap("aa:bb:cc:dd:ee:01", "AP1", ("02:00:00:00:00:A1", "Home"), ("", "Skipped")),
            Ap("aa:bb:cc:dd:ee:02", "AP2", ("02:00:00:00:00:a2", "vwire-0123456789abcdef")),
        });

        lookup.TryResolve("02:00:00:00:00:a1", null, out var a1).Should().BeTrue();
        a1.Should().Be(new SiteBssid("aa:bb:cc:dd:ee:01", "AP1", "Home"));
        lookup.TryResolve("02:00:00:00:00:A2", null, out var a2).Should().BeTrue();
        a2.ApName.Should().Be("AP2");
        lookup.TryResolve("02:00:00:00:00:ff", null, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:10", true)]   // the base MAC itself
    [InlineData("0a:bb:cc:dd:ee:12", true)]   // locally-administered first-octet variant
    [InlineData("aa:bb:cc:dd:ee:1f", true)]   // top of the 16-address block
    [InlineData("aa:bb:cc:dd:ee:20", false)]  // past the block
    [InlineData("aa:bb:cc:dd:ee:0f", false)]  // below the base
    [InlineData("aa:bb:cc:dd:ef:12", false)]  // octet 5 differs
    [InlineData("a8:bb:cc:dd:ee:12", false)]  // a different globally-administered OUI
    [InlineData("0b:bb:cc:dd:ee:12", false)]  // multicast
    public void IsDerivedFromApMac_matches_the_aps_bssid_block(string bssid, bool expected)
    {
        SiteBssidLookup.IsDerivedFromApMac(bssid, "aa:bb:cc:dd:ee:10").Should().Be(expected);
    }

    [Fact]
    public void A_mesh_vap_missing_from_every_vap_table_resolves_to_the_ap_it_derives_from()
    {
        var lookup = SiteBssidLookup.Build(new[]
        {
            Ap("aa:bb:cc:dd:ee:10", "AP1", ("aa:bb:cc:dd:ee:10", "Home")),
            Ap("aa:bb:cc:dd:ff:20", "AP2"),
        });
        var mesh = new NeighborNetwork { Bssid = "0a:bb:cc:dd:ee:12", Ssid = "vwire-0123456789abcdef" };

        lookup.Stamp(mesh);

        mesh.IsSiteBssid.Should().BeTrue();
        mesh.IsOwnNetwork.Should().BeTrue();
        mesh.OwnApMac.Should().Be("aa:bb:cc:dd:ee:10");
        mesh.OwnApName.Should().Be("AP1");
        mesh.OwnSsid.Should().Be("vwire-0123456789abcdef");
        SiteBssidLookup.IsMeshBackhaul(mesh).Should().BeTrue();
    }

    [Fact]
    public void Stamp_leaves_a_neighbors_unifi_ap_external()
    {
        var lookup = SiteBssidLookup.Build(new[] { Ap("aa:bb:cc:dd:ee:10", "AP1", ("aa:bb:cc:dd:ee:11", "Home")) });
        var neighbor = new NeighborNetwork { Bssid = "ac:bb:cc:00:00:01", Ssid = "NeighborCams" };

        lookup.Stamp(neighbor);

        neighbor.IsSiteBssid.Should().BeFalse();
        neighbor.IsOwnNetwork.Should().BeFalse();
        neighbor.OwnApMac.Should().BeNull();
    }

    [Theory]
    [InlineData("vwire-0123456789abcdef", null, true)]
    [InlineData("", "vwire-0123456789abcdef", true)]
    [InlineData("", "Home", false)]
    [InlineData("Home", null, false)]
    public void IsMeshBackhaul_reads_the_scanned_or_the_owning_essid(string ssid, string? ownSsid, bool expected)
    {
        SiteBssidLookup.IsMeshBackhaul(new NeighborNetwork { Ssid = ssid, OwnSsid = ownSsid }).Should().Be(expected);
    }
}
