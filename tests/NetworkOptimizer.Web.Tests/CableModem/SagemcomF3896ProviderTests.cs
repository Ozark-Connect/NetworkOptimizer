using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Components.Shared;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

/// <summary>
/// Parsing of the Sagemcom F3896LG /rest/v1/cablemodem API, against payloads captured from a
/// Virgin Media Hub 5 in modem mode.
/// </summary>
public class SagemcomF3896ProviderTests
{
    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "CableModem", "Fixtures");

    private static JsonElement Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, name))).RootElement.Clone();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static CmPollContext Context() => new()
    {
        Id = 1,
        Name = "Cable Modem",
        Host = "192.0.2.10",
        Port = 80,
    };

    private static CableModemStats ParseCaptured() => SagemcomF3896Provider.Parse(
        Fixture("sagemcom-f3896lg-downstream.json"),
        Fixture("sagemcom-f3896lg-upstream.json"),
        Context(),
        "Sagemcom F3896LG");

    [Fact]
    public void Parse_ReadsEveryChannelAsLocked()
    {
        var stats = ParseCaptured();

        stats.DownstreamChannels.Should().HaveCount(32);
        stats.UpstreamChannels.Should().HaveCount(6);
        stats.LockedDsChannels.Should().Be(32);
        stats.LockedUsChannels.Should().Be(6);
    }

    [Fact]
    public void Parse_ScQamDownstreamKeepsWholeUnits()
    {
        var channel = ParseCaptured().DownstreamChannels.Single(c => c.ChannelId == 2);

        channel.Modulation.Should().Be("QAM256");
        channel.Frequency.Should().Be(146_000_000);
        channel.Power.Should().Be(0.6);
        channel.Snr.Should().Be(40);
        channel.Correctables.Should().Be(647);
        channel.Uncorrectables.Should().Be(18);
    }

    // The API reports OFDM rxMer and power in tenths: 410 is 41.0 dB and -26 is -2.6 dBmV.
    [Fact]
    public void Parse_OfdmDownstreamLevelsAreTenths()
    {
        var channel = ParseCaptured().DownstreamChannels.Single(c => c.ChannelId == 33);

        channel.Modulation.Should().Be("OFDM");
        channel.Snr.Should().BeApproximately(41.0, 0.001);
        channel.Power.Should().BeApproximately(-2.6, 0.001);
        channel.Frequency.Should().Be(0);
        channel.Uncorrectables.Should().Be(101_898);
    }

    [Fact]
    public void Parse_OfdmaUpstreamPowerIsTenths()
    {
        var channel = ParseCaptured().UpstreamChannels.Single(c => c.ChannelId == 11);

        channel.ChannelType.Should().Be("OFDMA");
        channel.Power.Should().BeApproximately(41.2, 0.001);
        channel.Frequency.Should().Be(0);
    }

    [Fact]
    public void Parse_AtdmaUpstreamKeepsWholeUnitsAndStoresSymbolRateInSymbolsPerSecond()
    {
        var channel = ParseCaptured().UpstreamChannels.Single(c => c.ChannelId == 1);

        channel.ChannelType.Should().Be("ATDMA");
        channel.Frequency.Should().Be(49_600_000);
        channel.Power.Should().Be(46.5);
        channel.SymbolRate.Should().Be(5_120_000);
    }

    // Guards the scale: unscaled OFDM values (410 dB) would drag the average far above 40.
    [Fact]
    public void Parse_AggregatesStayInPhysicalRange()
    {
        var stats = ParseCaptured();

        stats.DownstreamSnrAvgDb.Should().BeInRange(39, 42);
        stats.DownstreamPowerAvgDbmv.Should().BeInRange(-3, 1);
        stats.UpstreamPowerAvgDbmv.Should().BeInRange(45, 47);
    }

    [Fact]
    public void Parse_UnlockedChannelIsNotCounted()
    {
        var downstream = Json("""
            {"downstream":{"channels":[
              {"channelType":"sc_qam","channelId":1,"frequency":138000000,"power":0,"modulation":"qam_256","snr":40,"rxMer":40,"correctedErrors":0,"uncorrectedErrors":0,"lockStatus":false}
            ]}}
            """);

        var stats = SagemcomF3896Provider.Parse(downstream, Json("{}"), Context(), "Sagemcom F3896LG");

        stats.DownstreamChannels.Single().LockStatus.Should().Be("Not Locked");
        stats.LockedDsChannels.Should().Be(0);
        stats.UpstreamChannels.Should().BeEmpty();
    }

    [Fact]
    public void FormatDeviceModel_NamesTheVendorForTheF3896LG()
    {
        var model = SagemcomF3896Provider.FormatDeviceModel(Json(
            """{"localization":{"skin":"virgin_media","productName":"Hub 5","modelName":"F3896LG"}}"""));

        model.Should().Be("Sagemcom F3896LG");
        CmStatsPanel.SplitModel(model).Should().Be(("Sagemcom", "F3896LG"));
    }

    [Fact]
    public void FormatDeviceModel_KeepsTheProductNameForAnUnknownModelCode()
    {
        SagemcomF3896Provider.FormatDeviceModel(Json(
                """{"localization":{"productName":"Hub 5","modelName":"F3897LG"}}"""))
            .Should().Be("Hub 5 (F3897LG)");
    }

    [Fact]
    public void FormatDeviceModel_FallsBackToTheVendorWhenNoModelIsReported()
    {
        SagemcomF3896Provider.FormatDeviceModel(Json("{}")).Should().Be("Sagemcom");
    }
}
