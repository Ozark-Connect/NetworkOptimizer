using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ClientWifiLinkTests
{
    [Fact]
    public void FromResult_CopiesTheStoredCapture()
    {
        var result = new Iperf3Result
        {
            ClientMac = "aa:bb:cc:dd:ee:ff",
            WifiRadio = "6e",
            WifiRadioProto = "ax",
            WifiChannel = 101,
            WifiChannelWidthMhz = 160,
            WifiSignalDbm = -70,
            WifiNoiseDbm = -92,
            WifiTxRateKbps = 1_729_000,
            WifiRxRateKbps = 1_152_000
        };

        var link = ClientWifiLink.FromResult(result);

        link.ClientMac.Should().Be("aa:bb:cc:dd:ee:ff");
        link.Radio.Should().Be("6e");
        link.RadioProto.Should().Be("ax");
        link.Channel.Should().Be(101);
        link.ChannelWidthMhz.Should().Be(160);
        link.SignalDbm.Should().Be(-70);
        link.NoiseDbm.Should().Be(-92);
        link.TxRateKbps.Should().Be(1_729_000);
        link.RxRateKbps.Should().Be(1_152_000);
        link.IsMlo.Should().BeFalse();
        link.MloLinks.Should().BeEmpty();
    }

    [Fact]
    public void FromResult_ParsesStoredMloLinks()
    {
        var result = new Iperf3Result
        {
            WifiIsMlo = true,
            WifiMloLinksJson = """[{"radio":"6e","channel":37,"channelWidth":320,"signal":-56,"noise":-95,"txRate":4804000,"rxRate":4323000},{"radio":"na","channel":40,"channelWidth":80,"signal":-44,"noise":null,"txRate":2402000,"rxRate":1729000}]"""
        };

        var link = ClientWifiLink.FromResult(result);

        link.IsMlo.Should().BeTrue();
        link.MloLinks.Should().HaveCount(2);
        link.MloLinks[0].Should().Be(new ClientWifiLinkBand("6e", 37, 320, -56, -95, 4_804_000, 4_323_000));
        link.MloLinks[1].NoiseDbm.Should().BeNull();
        link.MloLinks[1].RxRateKbps.Should().Be(1_729_000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void FromResult_UnreadableMloJson_GivesNoLinks(string? json)
    {
        var link = ClientWifiLink.FromResult(new Iperf3Result { WifiIsMlo = true, WifiMloLinksJson = json });

        link.IsMlo.Should().BeTrue();
        link.MloLinks.Should().BeEmpty();
    }

    [Fact]
    public void FromClient_WirelessClient_CopiesTheLiveLink()
    {
        var client = new ClientIdentity
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Band = "na",
            Protocol = "be",
            Channel = 40,
            ChannelWidth = 80,
            SignalDbm = -61,
            NoiseDbm = -96,
            TxRateKbps = 864_000,
            RxRateKbps = 720_000,
            IsMlo = true,
            MloLinks = [new MloLinkDetail { Radio = "na", Channel = 40, ChannelWidth = 80, Signal = -61, Noise = -96, TxRate = 864_000, RxRate = 720_000 }]
        };

        var link = ClientWifiLink.FromClient(client);

        link.Should().NotBeNull();
        link!.ClientMac.Should().Be("aa:bb:cc:dd:ee:ff");
        link.Radio.Should().Be("na");
        link.RadioProto.Should().Be("be");
        link.TxRateKbps.Should().Be(864_000);
        link.RxRateKbps.Should().Be(720_000);
        link.MloLinks.Should().ContainSingle()
            .Which.Should().Be(new ClientWifiLinkBand("na", 40, 80, -61, -96, 864_000, 720_000));
    }

    [Fact]
    public void FromClient_WiredOrMissingClient_IsNull()
    {
        ClientWifiLink.FromClient(null).Should().BeNull();
        ClientWifiLink.FromClient(new ClientIdentity { Mac = "00:11:22:33:44:55", IsWired = true }).Should().BeNull();
    }
}
