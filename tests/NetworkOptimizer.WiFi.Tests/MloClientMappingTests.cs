using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>
/// Mapping of Wi-Fi 7 MLO clients onto <see cref="WirelessClientSnapshot"/>. The mapper is internal
/// (see NetworkOptimizer.WiFi.csproj InternalsVisibleTo) so the per-client mapping can be exercised
/// without an API client.
/// </summary>
public class MloClientMappingTests
{
    private const string MldMac = "64:31:35:95:9e:5c";
    private const string ApMac = "00:11:22:33:44:55";

    private static readonly Dictionary<string, string> ApNames = new() { [ApMac] = "AP1" };
    private static readonly Dictionary<string, string> DisplayNames = new();

    /// <summary>
    /// One phone on three links: 6 GHz carries the traffic, 5 GHz and 2.4 GHz are negotiated but idle.
    /// </summary>
    private static UniFiClientResponse CreateMloClient(int? topLevelSignal = -61) => new()
    {
        Mac = MldMac,
        Name = "TestPhone",
        Ip = "192.0.2.10",
        ApMac = ApMac,
        Essid = "TestSSID",
        IsWired = false,
        Radio = "6e",
        RadioProto = "be",
        Channel = 85,
        ChannelWidth = 160,
        Signal = topLevelSignal,
        Noise = -96,
        Rssi = 35,
        TxRate = 1441000,
        RxRate = 1201000,
        IsMlo = true,
        MloDetails = new List<MloLinkDetail>
        {
            new()
            {
                Mac = "02:aa:bb:cc:dd:01",
                Radio = "6e",
                RadioProto = "be",
                Channel = 85,
                ChannelWidth = 160,
                Signal = -61,
                Noise = -96,
                Rssi = 35,
                Nss = 2,
                TxRate = 1441000,
                RxRate = 1201000,
                Satisfaction = 98
            },
            new()
            {
                Mac = "02:aa:bb:cc:dd:02",
                Radio = "na",
                RadioProto = "be",
                Channel = 128,
                ChannelWidth = 160,
                Signal = -95,
                Noise = -96,
                Rssi = 1,
                Nss = 2,
                TxRate = 0,
                RxRate = 0
            },
            new()
            {
                Mac = "02:aa:bb:cc:dd:03",
                Radio = "ng",
                RadioProto = "be",
                Channel = 1,
                ChannelWidth = 20,
                Signal = -39,
                Noise = -95,
                Rssi = 56,
                Nss = 2,
                TxRate = 0,
                RxRate = 0
            }
        }
    };

    private static WirelessClientSnapshot Map(UniFiClientResponse client) =>
        UniFiLiveDataProvider.MapToWirelessClientSnapshot(
            client, ApNames, DisplayNames, DateTimeOffset.UnixEpoch);

    [Fact]
    public void MloClient_MapsToOneSnapshotKeyedOnMldMac()
    {
        var snapshot = Map(CreateMloClient());

        snapshot.Mac.Should().Be(MldMac);
        snapshot.IsMlo.Should().BeTrue();
    }

    [Fact]
    public void MloClient_ScalarsDescribeActiveLink()
    {
        var snapshot = Map(CreateMloClient());

        snapshot.Signal.Should().Be(-61);
        snapshot.Channel.Should().Be(85);
        snapshot.ChannelWidth.Should().Be(160);
        snapshot.Band.Should().Be(RadioBand.Band6GHz);
        snapshot.Noise.Should().Be(-96);
        snapshot.Rssi.Should().Be(35);
        snapshot.TxRate.Should().Be(1441000);
        snapshot.RxRate.Should().Be(1201000);
    }

    [Fact]
    public void MloClient_KeepsAllThreeLinksInBreakdown()
    {
        var snapshot = Map(CreateMloClient());

        snapshot.MloLinks.Should().HaveCount(3);
        snapshot.MloLinks.Select(l => l.Band).Should().BeEquivalentTo(
            new[] { RadioBand.Band6GHz, RadioBand.Band5GHz, RadioBand.Band2_4GHz });

        var idle5g = snapshot.MloLinks.Single(l => l.Band == RadioBand.Band5GHz);
        idle5g.Signal.Should().Be(-95);
        idle5g.Channel.Should().Be(128);
        idle5g.ChannelWidth.Should().Be(160);
        idle5g.Mac.Should().Be("02:aa:bb:cc:dd:02");

        var idle2g = snapshot.MloLinks.Single(l => l.Band == RadioBand.Band2_4GHz);
        idle2g.Signal.Should().Be(-39);
        idle2g.Channel.Should().Be(1);

        var active6g = snapshot.MloLinks.Single(l => l.Band == RadioBand.Band6GHz);
        active6g.Nss.Should().Be(2);
        active6g.Satisfaction.Should().Be(98);
    }

    [Fact]
    public void MloClient_FallsBackToCarryingLink_WhenConsoleOmitsTopLevelSignal()
    {
        // The 2.4 GHz link is the strongest at -39 dBm; the 6 GHz link is the one passing traffic.
        var snapshot = Map(CreateMloClient(topLevelSignal: null));

        snapshot.Signal.Should().Be(-61);
        snapshot.Band.Should().Be(RadioBand.Band6GHz);
        snapshot.Channel.Should().Be(85);
        snapshot.ChannelWidth.Should().Be(160);
    }

    [Fact]
    public void MloClient_FallsBackToStrongestLink_WhenNoLinkReportsRates()
    {
        var client = CreateMloClient(topLevelSignal: null);
        foreach (var link in client.MloDetails!)
        {
            link.TxRate = 0;
            link.RxRate = 0;
        }

        var snapshot = Map(client);

        snapshot.Signal.Should().Be(-39);
        snapshot.Band.Should().Be(RadioBand.Band2_4GHz);
        snapshot.Channel.Should().Be(1);
    }

    [Fact]
    public void NonMloClient_MapsUnchanged()
    {
        var client = new UniFiClientResponse
        {
            Mac = "00:11:22:33:44:66",
            Name = "TestLaptop",
            Ip = "192.0.2.20",
            ApMac = ApMac,
            Essid = "TestSSID",
            IsWired = false,
            Radio = "na",
            RadioProto = "ax",
            Channel = 36,
            ChannelWidth = 80,
            Signal = -55,
            Noise = -94,
            Rssi = 39,
            TxRate = 600000,
            RxRate = 540000
        };

        var snapshot = Map(client);

        snapshot.IsMlo.Should().BeFalse();
        snapshot.MloLinks.Should().BeEmpty();
        snapshot.Band.Should().Be(RadioBand.Band5GHz);
        snapshot.Channel.Should().Be(36);
        snapshot.ChannelWidth.Should().Be(80);
        snapshot.Signal.Should().Be(-55);
        snapshot.Noise.Should().Be(-94);
        snapshot.Rssi.Should().Be(39);
        snapshot.TxRate.Should().Be(600000);
        snapshot.RxRate.Should().Be(540000);
        snapshot.ApName.Should().Be("AP1");
    }
}
