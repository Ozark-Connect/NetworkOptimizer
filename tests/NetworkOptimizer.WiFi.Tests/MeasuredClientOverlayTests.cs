using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class MeasuredClientOverlayTests
{
    private const string CoveredAp = "aa:bb:cc:dd:ee:01";
    private const string BareAp = "aa:bb:cc:dd:ee:02";

    private static WirelessClientSnapshot Console(
        string mac,
        string apMac,
        RadioBand band = RadioBand.Band2_4GHz,
        int? signal = -70,
        int? channel = 6,
        int? width = 20) => new()
        {
            Mac = mac,
            Name = "TestClient",
            ApMac = apMac,
            ApName = "AP One",
            Essid = "TestNet",
            Band = band,
            Channel = channel,
            ChannelWidth = width,
            Signal = signal,
            Noise = -95,
            Rssi = 25,
            TxRate = 100_000,
            RxRate = 90_000,
            Manufacturer = "TestVendor",
            IsOnline = true,
        };

    private static MeasuredWirelessClient Measured(
        string mac,
        string apMac,
        RadioBand band = RadioBand.Band5GHz,
        int? signal = -54,
        int? rssi = 38,
        params RadioBand[] observedBands) => new()
        {
            Mac = mac,
            ApMac = apMac,
            MeasuredAt = DateTimeOffset.UtcNow,
            Band = band,
            Channel = 44,
            ChannelWidth = 80,
            Signal = signal,
            Noise = -92,
            Rssi = rssi,
            TxRate = 2_161_800,
            RxRate = 1_080_900,
            Satisfaction = 97,
            ObservedBands = observedBands,
        };

    private static Dictionary<string, IReadOnlyList<MeasuredWirelessClient>> ByAp(
        params MeasuredWirelessClient[] clients) =>
        clients
            .GroupBy(c => c.ApMac)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MeasuredWirelessClient>)g.ToList());

    [Fact]
    public void LeavesEveryClientUntouched_WhenNothingIsMeasured()
    {
        var clients = new List<WirelessClientSnapshot> { Console("00:11:22:33:44:55", CoveredAp) };

        MeasuredClientOverlay.Apply(clients, new Dictionary<string, IReadOnlyList<MeasuredWirelessClient>>());

        clients[0].Band.Should().Be(RadioBand.Band2_4GHz);
        clients[0].Signal.Should().Be(-70);
        clients[0].Channel.Should().Be(6);
        clients[0].TxRate.Should().Be(100_000);
        clients[0].Capabilities.Supports5GHz.Should().BeFalse();
    }

    [Fact]
    public void OverlaysOnlyTheAccessPointsThatWereMeasured()
    {
        var onCovered = Console("00:11:22:33:44:55", CoveredAp);
        var onBare = Console("00:11:22:33:44:66", BareAp);
        var clients = new List<WirelessClientSnapshot> { onCovered, onBare };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("00:11:22:33:44:55", CoveredAp)));

        onCovered.Band.Should().Be(RadioBand.Band5GHz);
        onCovered.Signal.Should().Be(-54);
        onCovered.Channel.Should().Be(44);

        onBare.Band.Should().Be(RadioBand.Band2_4GHz);
        onBare.Signal.Should().Be(-70);
        onBare.Channel.Should().Be(6);
    }

    [Fact]
    public void KeepsConsoleIdentityFields()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("00:11:22:33:44:55", CoveredAp)));

        client.Name.Should().Be("TestClient");
        client.ApName.Should().Be("AP One");
        client.Essid.Should().Be("TestNet");
        client.Manufacturer.Should().Be("TestVendor");
    }

    [Fact]
    public void MapsSignalToDbmAndRssiToSnr()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("00:11:22:33:44:55", CoveredAp, signal: -54, rssi: 38)));

        client.Signal.Should().Be(-54);
        client.Rssi.Should().Be(38);
        client.Noise.Should().Be(-92);
        client.Snr.Should().Be(38);
    }

    [Fact]
    public void KeepsEveryLinkScalarOnTheSameLink()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        var clients = new List<WirelessClientSnapshot> { client };

        var measured = new MeasuredWirelessClient
        {
            Mac = "00:11:22:33:44:55",
            ApMac = CoveredAp,
            Band = RadioBand.Band6GHz,
            Signal = -48,
            Channel = null,
            ChannelWidth = null,
            TxRate = null,
            RxRate = null,
        };

        MeasuredClientOverlay.Apply(clients, ByAp(measured));

        // The console values described a 2.4 GHz link, so none of them may stand in for a 6 GHz one.
        client.Band.Should().Be(RadioBand.Band6GHz);
        client.Signal.Should().Be(-48);
        client.Channel.Should().BeNull();
        client.ChannelWidth.Should().BeNull();
        client.TxRate.Should().BeNull();
        client.RxRate.Should().BeNull();
    }

    [Fact]
    public void ProducesOneSnapshotForAnMloClientKeyedOnItsMldMac()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        client.IsMlo = true;
        client.MloLinks = new List<MloLinkSnapshot>
        {
            new() { Mac = "02:11:22:33:44:aa", Band = RadioBand.Band5GHz },
            new() { Mac = "02:11:22:33:44:bb", Band = RadioBand.Band6GHz },
        };
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("00:11:22:33:44:55", CoveredAp, RadioBand.Band6GHz)));

        clients.Should().HaveCount(1);
        client.Band.Should().Be(RadioBand.Band6GHz);
        client.Signal.Should().Be(-54);
        client.MloLinks.Should().HaveCount(2);
    }

    [Fact]
    public void ResolvesAnMloClientReportedUnderALinkMac()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        client.IsMlo = true;
        client.MloLinks = new List<MloLinkSnapshot> { new() { Mac = "02:11:22:33:44:aa", Band = RadioBand.Band5GHz } };
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("02:11:22:33:44:aa", CoveredAp)));

        clients.Should().HaveCount(1);
        client.Signal.Should().Be(-54);
    }

    [Fact]
    public void PopulatesBandSupportFromTheBandsSeen()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured(
            "00:11:22:33:44:55", CoveredAp, RadioBand.Band2_4GHz, -70, 22,
            RadioBand.Band2_4GHz, RadioBand.Band5GHz)));

        client.Capabilities.Supports2_4GHz.Should().BeTrue();
        client.Capabilities.Supports5GHz.Should().BeTrue();
        client.Capabilities.Supports6GHz.Should().BeFalse();
    }

    [Fact]
    public void ClaimsNoCapabilityTheSeriesNeverShowed()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("00:11:22:33:44:55", CoveredAp)));

        client.Capabilities.Supports5GHz.Should().BeFalse();
        client.Capabilities.MaxWifiGeneration.Should().BeNull();
        client.Capabilities.Supports11r.Should().BeNull();
        client.Capabilities.MaxNss.Should().BeNull();
    }

    [Fact]
    public void LeavesAClientWhoseConsoleRecordNamesAnotherAccessPoint()
    {
        var client = Console("00:11:22:33:44:55", BareAp);
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(Measured("00:11:22:33:44:55", CoveredAp)));

        client.Signal.Should().Be(-70);
        client.ApMac.Should().Be(BareAp);
    }

    [Fact]
    public void IgnoresAReadingWithNoBandOrNoSignal()
    {
        var client = Console("00:11:22:33:44:55", CoveredAp);
        var clients = new List<WirelessClientSnapshot> { client };

        MeasuredClientOverlay.Apply(clients, ByAp(
            new MeasuredWirelessClient { Mac = client.Mac, ApMac = CoveredAp, Band = RadioBand.Unknown, Signal = -54 }));
        client.Signal.Should().Be(-70);

        MeasuredClientOverlay.Apply(clients, ByAp(
            new MeasuredWirelessClient { Mac = client.Mac, ApMac = CoveredAp, Band = RadioBand.Band5GHz, Signal = null }));
        client.Signal.Should().Be(-70);
        client.Band.Should().Be(RadioBand.Band2_4GHz);
    }
}
