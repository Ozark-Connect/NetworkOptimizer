using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// Parser coverage for the two .jst firmware families this provider spans,
/// against DOCSIS tables captured from real gateways.
/// </summary>
public class XfinityGatewayProviderTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "CableModem", "Fixtures");

    private static XfinityGatewayProvider CreateProvider() =>
        new(NullLogger<XfinityGatewayProvider>.Instance);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDir, name));

    private static CmPollContext Context(
        string siteSlug = "default",
        int id = 1,
        string? statusPagePath = null,
        int port = 0) => new()
        {
            Id = id,
            SiteSlug = siteSlug,
            Name = "CM1",
            Host = "192.0.2.10",
            Port = port,
            StatusPagePath = statusPagePath,
        };

    [Fact]
    public void ParseNetworkSetup_ComcastBusiness_ReadsIndexLabelledChannels()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("comcast-business-cga4332.html"), Context());

        stats.DownstreamChannels.Should().HaveCount(29);
        stats.UpstreamChannels.Should().HaveCount(5);

        var first = stats.DownstreamChannels[0];
        first.ChannelId.Should().Be(1);
        first.LockStatus.Should().Be("Locked");
        first.Frequency.Should().Be(819_000_000);
        first.Snr.Should().Be(44.6);
        first.Power.Should().Be(10.7);
        first.Modulation.Should().Be("256 QAM");

        stats.DownstreamChannels[28].Modulation.Should().Be("OFDM");
        stats.DownstreamChannels[28].Frequency.Should().Be(900_000_000);
    }

    [Fact]
    public void ParseNetworkSetup_ComcastBusiness_ReadsUpstreamIncludingOfdma()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("comcast-business-cga4332.html"), Context());

        var scqam = stats.UpstreamChannels[0];
        scqam.ChannelId.Should().Be(1);
        scqam.Frequency.Should().Be(16_000_000);
        scqam.SymbolRate.Should().Be(5120);
        scqam.Power.Should().Be(41.0);
        scqam.ChannelType.Should().Be("ATDMA");

        var ofdma = stats.UpstreamChannels[4];
        ofdma.Frequency.Should().Be(36_000_000);
        ofdma.SymbolRate.Should().Be(0);
        ofdma.Power.Should().Be(35.0);
        ofdma.ChannelType.Should().Be("OFDMA");
    }

    [Fact]
    public void ParseNetworkSetup_Residential_TypesOfdmaFromModulationNotChannelType()
    {
        // The firmware's Channel Type row reads "TDMA" on the OFDMA channels.
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("xfinity-xb10-network-setup.html"), Context());

        stats.UpstreamChannels.Take(4).Should().OnlyContain(ch => ch.ChannelType == "ATDMA");
        stats.UpstreamChannels.Skip(4).Should().OnlyContain(ch => ch.ChannelType == "OFDMA");
    }

    [Fact]
    public void ParseNetworkSetup_ComcastBusiness_MergesCodewordsByPosition()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("comcast-business-cga4332.html"), Context());

        // This firmware's codeword table carries no Channel ID row, so the
        // columns line up with the downstream table by position alone.
        stats.DownstreamChannels[1].Correctables.Should().Be(312);
        stats.DownstreamChannels[1].Uncorrectables.Should().Be(1630);
        stats.DownstreamChannels[28].Correctables.Should().Be(4256604764);
        stats.DownstreamChannels[28].Uncorrectables.Should().Be(3992);
    }

    [Fact]
    public void ParseNetworkSetup_ComcastBusiness_DropsThePrimaryChannelsCopyOfTheOfdmCounters()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("comcast-business-cga4332.html"), Context());

        stats.DownstreamChannels[0].Correctables.Should().Be(0);
        stats.DownstreamChannels[0].Uncorrectables.Should().Be(0);
        stats.TotalUncorrectables.Should().Be(stats.DownstreamChannels.Skip(1).Sum(ch => ch.Uncorrectables));
    }

    [Fact]
    public void ParseNetworkSetup_KeepsPrimaryCodewordsThatDoNotMatchAnOfdmChannel()
    {
        // Only an exact copy of an OFDM channel's counters is the firmware echo.
        var html = ReplaceLast(Fixture("comcast-business-cga4332.html"), "4256604764", "4256604765");

        var stats = CreateProvider().ParseNetworkSetup(html, Context());

        stats.DownstreamChannels[0].Correctables.Should().Be(4256604764);
        stats.DownstreamChannels[0].Uncorrectables.Should().Be(3992);
    }

    private static string ReplaceLast(string text, string oldValue, string newValue)
    {
        var index = text.LastIndexOf(oldValue, StringComparison.Ordinal);
        return text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    [Fact]
    public void ParseNetworkSetup_Residential_StillReadsChannelIdLabelledTables()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("xfinity-xb10-network-setup.html"), Context());

        stats.DownstreamChannels.Should().HaveCount(10);
        stats.UpstreamChannels.Should().HaveCount(8);

        // Residential firmware reports real DOCSIS channel IDs, not positions.
        stats.DownstreamChannels[0].ChannelId.Should().Be(17);
        stats.DownstreamChannels[0].Snr.Should().Be(48.7);

        // Channel 17 is the primary; its column repeats OFDM channel 194's counters.
        stats.DownstreamChannels[0].Correctables.Should().Be(0);
        var ofdm = stats.DownstreamChannels.Single(ch => ch.ChannelId == 194);
        ofdm.Correctables.Should().Be(509057704);
        ofdm.Uncorrectables.Should().Be(0);
    }

    [Fact]
    public void ParseNetworkSetup_Xb7_ReadsDownstreamChannels()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("xfinity-xb7-cgm4331-network-setup.html"), Context());

        stats.DownstreamChannels.Should().HaveCount(34);

        var primary = stats.DownstreamChannels[0];
        primary.ChannelId.Should().Be(18);
        primary.LockStatus.Should().Be("Locked");
        primary.Frequency.Should().Be(663_000_000);
        primary.Snr.Should().Be(39.5);
        primary.Power.Should().Be(-11.7);
        primary.Modulation.Should().Be("256 QAM");

        stats.DownstreamChannels.Where(ch => ch.Modulation == "OFDM")
            .Select(ch => ch.Frequency)
            .Should().Equal(350_000_000, 920_000_000);
    }

    [Fact]
    public void ParseNetworkSetup_Xb7_ReadsUpstreamIncludingOfdma()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("xfinity-xb7-cgm4331-network-setup.html"), Context());

        stats.UpstreamChannels.Should().HaveCount(5);

        var scqam = stats.UpstreamChannels[0];
        scqam.ChannelId.Should().Be(1);
        scqam.Frequency.Should().Be(21_000_000);
        scqam.SymbolRate.Should().Be(2560);
        scqam.Power.Should().Be(49.3);
        scqam.ChannelType.Should().Be("TDMA_AND_ATDMA");

        // The firmware's Channel Type row reads "TDMA" on this column; Modulation says OFDMA.
        var ofdma = stats.UpstreamChannels[4];
        ofdma.ChannelId.Should().Be(10);
        ofdma.Frequency.Should().Be(42_000_000);
        ofdma.SymbolRate.Should().Be(0);
        ofdma.Power.Should().Be(47.7);
        ofdma.ChannelType.Should().Be("OFDMA");
    }

    [Fact]
    public void ParseNetworkSetup_Xb7_MergesCodewordsByChannelIdAndClearsThePrimaryEcho()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("xfinity-xb7-cgm4331-network-setup.html"), Context());

        var ch24 = stats.DownstreamChannels.Single(ch => ch.ChannelId == 24);
        ch24.Correctables.Should().Be(18856);
        ch24.Uncorrectables.Should().Be(30224);

        // Channel 18 is the primary; its column repeats OFDM channel 34's counters.
        stats.DownstreamChannels[0].Correctables.Should().Be(0);
        stats.DownstreamChannels[0].Uncorrectables.Should().Be(0);
        var ofdm = stats.DownstreamChannels.Single(ch => ch.ChannelId == 34);
        ofdm.Correctables.Should().Be(3870117303);
        ofdm.Uncorrectables.Should().Be(4183);
    }

    [Theory]
    [InlineData("comcast-business-cga4332.html", "CBR (CGA4332COM)")]
    [InlineData("xfinity-xb10-network-setup.html", "XB10 (SG417DBCT)")]
    [InlineData("xfinity-xb7-cgm4331-network-setup.html", "XB7 (CGM4331COM)")]
    public void ParseNetworkSetup_PairsProductTypeWithTheModelNumber(string fixture, string expected)
    {
        var stats = CreateProvider().ParseNetworkSetup(Fixture(fixture), Context());

        stats.DeviceModel.Should().Be(expected);
    }

    [Fact]
    public void CandidatePaths_TriesResidentialPageBeforeBusinessPage()
    {
        CreateProvider().CandidatePaths(Context())
            .Should().Equal("/network_setup.jst", "/comcast_network.jst");
    }

    [Fact]
    public void CandidatePaths_PutsAnExplicitOverrideFirstWithoutDroppingTheDefaults()
    {
        CreateProvider().CandidatePaths(Context(statusPagePath: "/custom.jst"))
            .Should().Equal("/custom.jst", "/network_setup.jst", "/comcast_network.jst");
    }

    [Fact]
    public void CandidatePaths_LeadsWithThePathThatWorkedLastTime()
    {
        var provider = CreateProvider();
        var context = Context();

        provider.Remember(context, "/comcast_network.jst");

        provider.CandidatePaths(context)
            .Should().Equal("/comcast_network.jst", "/network_setup.jst");
    }

    [Fact]
    public void CandidatePaths_DoesNotShareDiscoveryAcrossSitesWithTheSameConfigId()
    {
        var provider = CreateProvider();
        provider.Remember(Context(siteSlug: "site-a", id: 1), "/comcast_network.jst");

        provider.CandidatePaths(Context(siteSlug: "site-b", id: 1))
            .Should().Equal("/network_setup.jst", "/comcast_network.jst");
    }

    [Fact]
    public void CandidatePaths_ForgetsDiscoveryWhenTheOverrideChanges()
    {
        var provider = CreateProvider();
        provider.Remember(Context(), "/comcast_network.jst");

        provider.CandidatePaths(Context(statusPagePath: "/custom.jst"))
            .Should().Equal("/custom.jst", "/network_setup.jst", "/comcast_network.jst");
    }

    [Theory]
    [InlineData(0, false, "http://192.0.2.10")]
    [InlineData(80, false, "http://192.0.2.10")]
    [InlineData(8080, false, "http://192.0.2.10:8080")]
    [InlineData(443, true, "https://192.0.2.10")]
    [InlineData(0, true, "https://192.0.2.10")]
    [InlineData(80, true, "https://192.0.2.10")]
    [InlineData(8443, true, "https://192.0.2.10:8443")]
    public void BuildBaseUrl_MapsSchemeAndPortToOrigin(int port, bool useHttps, string expected)
    {
        XfinityGatewayProvider.BuildBaseUrl(Context(port: port), useHttps).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(80, false)]
    [InlineData(8080, false)]
    [InlineData(443, true)]
    public void StartsWithHttps_OnlyForPort443BeforeAnythingIsLearned(int port, bool expected)
    {
        CreateProvider().StartsWithHttps(Context(port: port)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(80, false, true)]
    [InlineData(80, true, false)]
    [InlineData(443, true, false)]
    [InlineData(8080, false, false)]
    public void CanFallBackToHttps_OnlyFromHttpOnTheDefaultPort(int port, bool useHttps, bool expected)
    {
        XfinityGatewayProvider.CanFallBackToHttps(Context(port: port), useHttps).Should().Be(expected);
    }

    [Fact]
    public void StartsWithHttps_AfterHttpsServedThisModem()
    {
        var provider = CreateProvider();
        var context = Context();

        provider.RememberScheme(context, useHttps: true);

        provider.StartsWithHttps(context).Should().BeTrue();
    }

    [Fact]
    public void StartsWithHttps_StaysOnHttpWhenHttpServedThisModem()
    {
        var provider = CreateProvider();
        var context = Context();

        provider.RememberScheme(context, useHttps: false);

        provider.StartsWithHttps(context).Should().BeFalse();
    }

    [Fact]
    public void StartsWithHttps_ProbesAgainAfterTheSchemeIsForgotten()
    {
        var provider = CreateProvider();
        var context = Context();
        provider.RememberScheme(context, useHttps: true);

        provider.ForgetScheme(context);

        provider.StartsWithHttps(context).Should().BeFalse();
    }

    [Fact]
    public void StartsWithHttps_ProbesAgainWhenThePortSettingChanges()
    {
        var provider = CreateProvider();
        provider.RememberScheme(Context(port: 0), useHttps: true);

        provider.StartsWithHttps(Context(port: 80)).Should().BeFalse();
    }

    [Fact]
    public void StartsWithHttps_DoesNotShareTheSchemeAcrossSitesWithTheSameConfigId()
    {
        var provider = CreateProvider();
        provider.RememberScheme(Context(siteSlug: "site-a", id: 1), useHttps: true);

        provider.StartsWithHttps(Context(siteSlug: "site-b", id: 1)).Should().BeFalse();
    }
}
