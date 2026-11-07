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

        lookup.Should().HaveCount(2);
        lookup["02:00:00:00:00:a1"].Should().Be(new SiteBssid("aa:bb:cc:dd:ee:01", "AP1", "Home"));
        lookup["02:00:00:00:00:A2"].ApName.Should().Be("AP2");
    }

    [Fact]
    public void Stamp_sets_the_owner_only_for_a_site_bssid()
    {
        var lookup = SiteBssidLookup.Build(new[] { Ap("aa:bb:cc:dd:ee:01", "AP1", ("02:00:00:00:00:a1", "Home")) });
        var own = new NeighborNetwork { Bssid = "02:00:00:00:00:A1" };
        var other = new NeighborNetwork { Bssid = "02:00:00:00:00:ff", IsOwnNetwork = true };

        SiteBssidLookup.Stamp(own, lookup);
        SiteBssidLookup.Stamp(other, lookup);

        own.IsSiteBssid.Should().BeTrue();
        own.OwnApMac.Should().Be("aa:bb:cc:dd:ee:01");
        own.OwnSsid.Should().Be("Home");
        other.IsSiteBssid.Should().BeFalse();
        other.OwnApMac.Should().BeNull();
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
