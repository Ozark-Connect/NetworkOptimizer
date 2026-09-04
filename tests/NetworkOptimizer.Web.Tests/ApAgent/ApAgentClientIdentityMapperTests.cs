using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Turning an access point's client record into what Client Performance shows. The band and
/// protocol spellings are the console's, because the band display, the band classes and the radio
/// matching all key on them, so a source swap must not change a single rendered string.
/// </summary>
public class ApAgentClientIdentityMapperTests
{
    private const string ApMac = "aa:bb:cc:11:22:01";
    private const string ClientMac = "aa:bb:cc:dd:ee:ff";
    private const string MldMac = "aa:bb:cc:dd:ee:f0";

    [Theory]
    [InlineData("2.4", "ng")]
    [InlineData("5", "na")]
    [InlineData("6", "6e")]
    [InlineData("6GHz", "6e")]
    public void Bands_are_mapped_to_the_spelling_the_page_already_uses(string token, string expected)
        => ApAgentClientIdentityMapper.MapBand(token).Should().Be(expected);

    [Fact]
    public void An_unrecognized_band_is_not_guessed_at()
        => ApAgentClientIdentityMapper.MapBand("60").Should().BeNull();

    [Theory]
    [InlineData("IEEE80211_MODE_11BE_EHT320", "na", "be")]
    [InlineData("IEEE80211_MODE_11AXA_HE160", "na", "ax")]
    [InlineData("IEEE80211_MODE_11AC_VHT80", "na", "ac")]
    [InlineData("IEEE80211_MODE_11NA_HT40PLUS", "na", "na")]
    [InlineData("IEEE80211_MODE_11NG_HT20", "ng", "ng")]
    [InlineData("IEEE80211_MODE_11G", "ng", "ng")]
    public void Phy_modes_become_the_consoles_radio_protocol(string mode, string band, string expected)
        => ApAgentClientIdentityMapper.MapProtocol(mode, band).Should().Be(expected);

    [Fact]
    public void An_unreadable_phy_mode_leaves_the_protocol_to_the_console()
        => ApAgentClientIdentityMapper.MapProtocol("IEEE80211_MODE_SOMETHING_NEW", "na").Should().BeNull();

    [Fact]
    public void A_plain_client_carries_its_live_fields_and_no_mlo_detail()
    {
        var client = new ApAgentClient
        {
            Key = ClientMac,
            Mac = ClientMac,
            Band = "5",
            Channel = 44,
            Bandwidth = 80,
            Signal = -58,
            Noise = -96,
            TxRateKbps = 780_000,
            RxRateKbps = 620_000,
            Satisfaction = 94,
            Links = { new ApAgentClientLink { Mac = ClientMac, Active = true, Band = "5", Mode = "IEEE80211_MODE_11AC_VHT80" } },
        };

        var identity = ApAgentClientIdentityMapper.ToLiveIdentity(client, ApMac);

        identity.Should().NotBeNull();
        identity!.Band.Should().Be("na");
        identity.BandDisplay.Should().Be("5 GHz");
        identity.Protocol.Should().Be("ac");
        identity.Channel.Should().Be(44);
        identity.ChannelWidth.Should().Be(80);
        identity.SignalDbm.Should().Be(-58);
        identity.NoiseDbm.Should().Be(-96);
        identity.TxRateKbps.Should().Be(780_000);
        identity.RxRateKbps.Should().Be(620_000);
        identity.Satisfaction.Should().Be(94);
        identity.ApMac.Should().Be(ApMac);
        identity.HasApAgentData.Should().BeTrue();
        identity.IsMlo.Should().BeFalse();
        identity.MloLinks.Should().BeNull("a client that is not multi-link must not render the MLO pill");
    }

    [Fact]
    public void An_mlo_client_is_keyed_on_its_mld_mac_and_keeps_every_link()
    {
        var client = new ApAgentClient
        {
            Key = MldMac,
            Mac = MldMac,
            MldMac = MldMac,
            IsMlo = true,
            Band = "6",
            Channel = 37,
            Bandwidth = 160,
            Signal = -61,
            Links =
            {
                new ApAgentClientLink
                {
                    Mac = "aa:bb:cc:dd:ee:f1", Band = "5", Channel = 44, Bandwidth = 80,
                    Active = false, Signal = -95, Snr = 1, TxRateKbps = 0, RxRateKbps = 0,
                },
                new ApAgentClientLink
                {
                    Mac = "aa:bb:cc:dd:ee:f2", Band = "6", Channel = 37, Bandwidth = 160,
                    Active = true, Signal = -61, Snr = 35, Nss = 2,
                    TxRateKbps = 1_400_000, RxRateKbps = 1_100_000,
                    Mode = "IEEE80211_MODE_11BE_EHT320",
                },
            },
        };

        var identity = ApAgentClientIdentityMapper.ToLiveIdentity(client, ApMac);

        identity.Should().NotBeNull();
        identity!.Mac.Should().Be(MldMac, "the links are one client, not one client each");
        identity.IsMlo.Should().BeTrue();
        identity.SignalDbm.Should().Be(-61, "the scalars describe the active link");
        identity.Protocol.Should().Be("be");
        identity.MloLinks.Should().HaveCount(2);

        var idle = identity.MloLinks!.Single(l => l.Radio == "na");
        idle.ActiveLink.Should().BeFalse();
        idle.Signal.Should().Be(-95);

        var active = identity.MloLinks!.Single(l => l.Radio == "6e");
        active.ActiveLink.Should().BeTrue();
        active.Signal.Should().Be(-61);
        active.Rssi.Should().Be(35, "signal is dBm and rssi is the ratio above the noise floor");
        active.Nss.Should().Be(2);
        active.TxRate.Should().Be(1_400_000);
    }

    [Fact]
    public void A_client_the_access_point_cannot_place_on_a_band_is_left_to_the_console()
    {
        var client = new ApAgentClient { Key = ClientMac, Mac = ClientMac, Band = "60" };

        ApAgentClientIdentityMapper.ToLiveIdentity(client, ApMac).Should().BeNull();
    }
}
