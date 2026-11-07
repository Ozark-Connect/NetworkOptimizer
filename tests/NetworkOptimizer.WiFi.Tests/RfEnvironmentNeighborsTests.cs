using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class RfEnvironmentNeighborsTests
{
    private const string Ap1 = "aa:bb:cc:dd:ee:01";
    private const string Ap2 = "aa:bb:cc:dd:ee:02";
    private const string Ap3 = "aa:bb:cc:dd:ee:03";

    private static ChannelScanResult Scan(string apMac, string apName, params NeighborNetwork[] neighbors) => new()
    {
        ApMac = apMac, ApName = apName, Band = RadioBand.Band5GHz, Neighbors = neighbors.ToList()
    };

    private static NeighborNetwork External(string bssid, int signal, int channel = 36) => new()
    {
        Bssid = bssid, Ssid = "Neighbor", Channel = channel, Signal = signal
    };

    private static NeighborNetwork Own(string bssid, string ownerMac, int signal, string ownSsid = "Home") => new()
    {
        Bssid = bssid, Ssid = ownSsid, Channel = 44, Signal = signal, IsOwnNetwork = true,
        IsSiteBssid = true, OwnApMac = ownerMac, OwnApName = ownerMac == Ap1 ? "AP1" : "AP2", OwnSsid = ownSsid
    };

    [Fact]
    public void With_no_site_bssids_the_external_list_is_every_neighbor_and_the_site_list_is_empty()
    {
        var scans = new List<ChannelScanResult>
        {
            Scan(Ap1, "AP1", External("02:00:00:00:00:01", -70), External("02:00:00:00:00:02", -80),
                External("02:00:00:00:00:03", -60, channel: 65535)),
            Scan(Ap2, "AP2", External("02:00:00:00:00:01", -55)),
        };

        var external = RfEnvironmentNeighbors.ExternalNeighbors(scans);

        external.Should().HaveCount(2, "the out-of-range channel is dropped, the shared BSSID merged");
        var shared = external.Single(r => r.Network.Bssid == "02:00:00:00:00:01");
        shared.Network.Signal.Should().Be(-55);
        shared.ApName.Should().Be("AP2");
        shared.DetectedByAps.Should().BeEquivalentTo("AP1", "AP2");
        shared.SignalByApMac.Should().BeEquivalentTo(new Dictionary<string, int> { [Ap1] = -70, [Ap2] = -55 });
        shared.SiteAp.Should().BeNull();
        RfEnvironmentNeighbors.SiteAccessPoints(scans).Should().BeEmpty();
    }

    [Fact]
    public void Site_bssids_leave_the_external_list()
    {
        var scans = new List<ChannelScanResult>
        {
            Scan(Ap1, "AP1", External("02:00:00:00:00:01", -70), Own("02:00:00:00:00:a2", Ap2, -50)),
        };

        RfEnvironmentNeighbors.ExternalNeighbors(scans)
            .Should().ContainSingle(r => r.Network.Bssid == "02:00:00:00:00:01");
    }

    [Fact]
    public void A_bssid_flagged_in_any_sighting_is_excluded_everywhere()
    {
        var unflagged = External("02:00:00:00:00:a2", -40);
        var scans = new List<ChannelScanResult>
        {
            Scan(Ap1, "AP1", Own("02:00:00:00:00:a2", Ap2, -50)),
            Scan(Ap3, "AP3", unflagged),
        };

        RfEnvironmentNeighbors.ExternalNeighbors(scans).Should().BeEmpty();
        var site = RfEnvironmentNeighbors.SiteAccessPoints(scans).Single();
        site.SiteAp!.ApMac.Should().Be(Ap2);
        site.DetectedByAps.Should().BeEquivalentTo("AP1", "AP3");
    }

    [Fact]
    public void Site_access_points_keep_sibling_sightings_and_drop_an_ap_hearing_itself()
    {
        var scans = new List<ChannelScanResult>
        {
            Scan(Ap1, "AP1", Own("02:00:00:00:00:a1", Ap1, -20), Own("02:00:00:00:00:a2", Ap2, -60)),
            Scan(Ap2, "AP2", Own("02:00:00:00:00:a2", Ap2, -15), Own("02:00:00:00:00:a1", Ap1, -65)),
            Scan(Ap3, "AP3", Own("02:00:00:00:00:a3", Ap3, -10)),
        };

        var site = RfEnvironmentNeighbors.SiteAccessPoints(scans);

        site.Should().HaveCount(2, "AP3 heard only itself");
        var a2 = site.Single(r => r.Network.Bssid == "02:00:00:00:00:a2");
        a2.Network.Signal.Should().Be(-60, "AP2's -15 dBm reading of itself never counts");
        a2.DetectedByAps.Should().Equal("AP1");
        a2.SiteAp!.ApName.Should().Be("AP2");
        var a1 = site.Single(r => r.Network.Bssid == "02:00:00:00:00:a1");
        a1.DetectedByAps.Should().Equal("AP2");
        RfEnvironmentNeighbors.ExternalNeighbors(scans).Should().BeEmpty();
    }
}
