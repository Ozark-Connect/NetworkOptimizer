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
        string? statusPagePath = null) => new()
        {
            Id = id,
            SiteSlug = siteSlug,
            Name = "CM1",
            Host = "192.0.2.10",
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
    }

    [Fact]
    public void ParseNetworkSetup_ComcastBusiness_MergesCodewordsByPosition()
    {
        var stats = CreateProvider()
            .ParseNetworkSetup(Fixture("comcast-business-cga4332.html"), Context());

        // This firmware's codeword table carries no Channel ID row, so the
        // columns line up with the downstream table by position alone.
        stats.DownstreamChannels[0].Correctables.Should().Be(4256604764);
        stats.DownstreamChannels[0].Uncorrectables.Should().Be(3992);
        stats.DownstreamChannels[1].Correctables.Should().Be(312);
        stats.DownstreamChannels[1].Uncorrectables.Should().Be(1630);
        stats.DownstreamChannels[28].Uncorrectables.Should().Be(3992);
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
        stats.DownstreamChannels[0].Correctables.Should().Be(509057704);
        stats.DownstreamChannels[0].Uncorrectables.Should().Be(0);
    }

    [Theory]
    [InlineData("comcast-business-cga4332.html", "CBR (CGA4332COM)")]
    [InlineData("xfinity-xb10-network-setup.html", "XB10 (SG417DBCT)")]
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
}
