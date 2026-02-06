using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class RegulatoryChannelDataTests
{
    /// <summary>
    /// Builds a minimal stat/current-channel JSON matching the real API structure.
    /// </summary>
    private static JsonElement BuildTestJson(
        int[]? channelsNg = null,
        int[]? channelsNg40 = null,
        int[]? channelsNa = null,
        int[]? channelsNa40 = null,
        int[]? channelsNa80 = null,
        int[]? channelsNa160 = null,
        int[]? channelsNa240 = null,
        int[]? channelsNaDfs = null,
        int[]? channels6e = null,
        int[]? channels6e40 = null,
        int[]? channels6e80 = null,
        int[]? channels6e160 = null,
        int[]? channels6e320 = null)
    {
        var obj = new Dictionary<string, object>();

        if (channelsNg != null) obj["channels_ng"] = channelsNg;
        if (channelsNg40 != null) obj["channels_ng_40"] = channelsNg40;
        if (channelsNa != null) obj["channels_na"] = channelsNa;
        if (channelsNa40 != null) obj["channels_na_40"] = channelsNa40;
        if (channelsNa80 != null) obj["channels_na_80"] = channelsNa80;
        if (channelsNa160 != null) obj["channels_na_160"] = channelsNa160;
        if (channelsNa240 != null) obj["channels_na_240"] = channelsNa240;
        if (channelsNaDfs != null) obj["channels_na_dfs"] = channelsNaDfs;
        if (channels6e != null) obj["channels_6e"] = channels6e;
        if (channels6e40 != null) obj["channels_6e_40"] = channels6e40;
        if (channels6e80 != null) obj["channels_6e_80"] = channels6e80;
        if (channels6e160 != null) obj["channels_6e_160"] = channels6e160;
        if (channels6e320 != null) obj["channels_6e_320"] = channels6e320;

        var json = JsonSerializer.Serialize(obj);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public class Parse
    {
        [Fact]
        public void Parses2_4GHzChannels()
        {
            var element = BuildTestJson(
                channelsNg: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
                channelsNg40: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

            var result = RegulatoryChannelData.Parse(element);

            result.Should().NotBeNull();
            result!.Channels2_4GHz[20].Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            result.Channels2_4GHz[40].Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        }

        [Fact]
        public void Parses5GHzChannelsAtMultipleWidths()
        {
            var element = BuildTestJson(
                channelsNa: [36, 40, 44, 48, 52, 56, 60, 64, 149, 153, 157, 161, 165],
                channelsNa40: [36, 40, 44, 48, 52, 56, 60, 64, 149, 153, 157, 161],
                channelsNa80: [36, 40, 44, 48, 52, 56, 60, 64, 149, 153, 157, 161],
                channelsNa160: [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128],
                channelsNaDfs: [52, 56, 60, 64, 100, 104, 108, 112]);

            var result = RegulatoryChannelData.Parse(element);

            result.Should().NotBeNull();
            result!.Channels5GHz[20].Should().Contain(165);
            result.Channels5GHz[40].Should().NotContain(165);
            result.Channels5GHz[160].Should().Contain(128).And.NotContain(149);
            result.DfsChannels.Should().Equal(52, 56, 60, 64, 100, 104, 108, 112);
        }

        [Fact]
        public void Parses6GHzChannelsAtMultipleWidths()
        {
            var element = BuildTestJson(
                channels6e: [1, 5, 9, 13, 17, 21, 25, 29, 33, 37],
                channels6e320: [1, 5, 9, 13, 17, 21, 25, 29]);

            var result = RegulatoryChannelData.Parse(element);

            result.Should().NotBeNull();
            result!.Channels6GHz[20].Should().HaveCount(10);
            result.Channels6GHz[320].Should().HaveCount(8);
            result.Channels6GHz[320].Should().NotContain(33);
        }

        [Fact]
        public void HandlesEmptyJson()
        {
            var json = "{}";
            using var doc = JsonDocument.Parse(json);
            var result = RegulatoryChannelData.Parse(doc.RootElement);

            result.Should().NotBeNull();
            result!.Channels2_4GHz[20].Should().BeEmpty();
            result.Channels5GHz[20].Should().BeEmpty();
            result.Channels6GHz[20].Should().BeEmpty();
            result.DfsChannels.Should().BeEmpty();
        }

        [Fact]
        public void HandlesPartialData()
        {
            var element = BuildTestJson(channelsNg: [1, 6, 11]);

            var result = RegulatoryChannelData.Parse(element);

            result.Should().NotBeNull();
            result!.Channels2_4GHz[20].Should().Equal(1, 6, 11);
            result.Channels5GHz[20].Should().BeEmpty();
            result.Channels6GHz[20].Should().BeEmpty();
        }
    }

    public class GetChannels
    {
        private static RegulatoryChannelData CreateUsData()
        {
            return new RegulatoryChannelData
            {
                Channels2_4GHz = new Dictionary<int, int[]>
                {
                    [20] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
                    [40] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]
                },
                Channels5GHz = new Dictionary<int, int[]>
                {
                    [20] = [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165],
                    [40] = [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161],
                    [80] = [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161],
                    [160] = [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128]
                },
                DfsChannels = [52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144],
                Channels6GHz = new Dictionary<int, int[]>
                {
                    [20] = [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61],
                    [160] = [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61],
                    [320] = [1, 5, 9, 13, 17, 21, 25, 29]
                },
                PscChannels6GHz = [5, 21, 37, 53, 69, 85, 101, 117, 133, 149, 165, 181, 197, 213, 229]
            };
        }

        [Theory]
        [InlineData(20, 25)] // All 5 GHz including DFS
        [InlineData(40, 24)] // No 165 at 40 MHz
        [InlineData(160, 16)] // Only 36-128 at 160 MHz
        public void Returns5GHzChannelsAtCorrectWidth(int width, int expectedCount)
        {
            var data = CreateUsData();
            var channels = data.GetChannels(RadioBand.Band5GHz, width);
            channels.Should().HaveCount(expectedCount);
        }

        [Fact]
        public void Excludes5GHzDfsChannelsWhenRequested()
        {
            var data = CreateUsData();
            var channels = data.GetChannels(RadioBand.Band5GHz, 20, includeDfs: false);

            channels.Should().Contain(36);
            channels.Should().Contain(149);
            channels.Should().NotContain(52);
            channels.Should().NotContain(100);
            channels.Should().NotContain(144);
        }

        [Fact]
        public void Excludes5GHzDfsAtSpecificWidth()
        {
            var data = CreateUsData();

            // At 160 MHz without DFS, only UNII-1 remains (36-48)
            var channels = data.GetChannels(RadioBand.Band5GHz, 160, includeDfs: false);
            channels.Should().OnlyContain(ch => ch >= 36 && ch <= 48);
        }

        [Fact]
        public void Returns6GHzPscChannelsOnly()
        {
            var data = CreateUsData();

            // 6 GHz at 320 MHz: PSC channels intersected with width-valid channels
            // Width list has [1,5,9,13,17,21,25,29], PSC is [5,21,37,53,...229]
            // Intersection: [5, 21]
            var ch320 = data.GetChannels(RadioBand.Band6GHz, 320);
            ch320.Should().Equal(5, 21);

            // 6 GHz at 20 MHz: PSC channels intersected with all channels
            // Width list has [1,5,9,...61], PSC starts at 5 with step 16
            var ch20 = data.GetChannels(RadioBand.Band6GHz, 20);
            ch20.Should().Contain(5);
            ch20.Should().Contain(21);
            ch20.Should().Contain(37);
            ch20.Should().Contain(53);
            ch20.Should().NotContain(1); // Not PSC
            ch20.Should().NotContain(9); // Not PSC
        }

        [Fact]
        public void Returns2_4GHzChannels()
        {
            var data = CreateUsData();
            var channels = data.GetChannels(RadioBand.Band2_4GHz, 20);
            channels.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        }

        [Fact]
        public void FallsBackToBase20MHzForUnknownWidth()
        {
            var data = CreateUsData();

            // No 240 MHz entry for 5 GHz in test data, should fall back to 20 MHz
            var channels = data.GetChannels(RadioBand.Band5GHz, 240);
            channels.Should().HaveCount(25); // Falls back to 20 MHz list
        }

        [Fact]
        public void ReturnsEmptyForUnknownBand()
        {
            var data = CreateUsData();
            var channels = data.GetChannels((RadioBand)99, 20);
            channels.Should().BeEmpty();
        }

        [Fact]
        public void DfsFilterDoesNotAffect2_4GHz()
        {
            var data = CreateUsData();
            var withDfs = data.GetChannels(RadioBand.Band2_4GHz, 20, includeDfs: true);
            var withoutDfs = data.GetChannels(RadioBand.Band2_4GHz, 20, includeDfs: false);
            withDfs.Should().Equal(withoutDfs);
        }

        [Fact]
        public void DfsFilterDoesNotAffect6GHz()
        {
            var data = CreateUsData();
            var withDfs = data.GetChannels(RadioBand.Band6GHz, 20, includeDfs: true);
            var withoutDfs = data.GetChannels(RadioBand.Band6GHz, 20, includeDfs: false);
            // Both should be PSC-filtered and identical (DFS doesn't apply to 6 GHz)
            withDfs.Should().Equal(withoutDfs);
        }

        [Fact]
        public void Returns6GHzAllChannelsWhenNoPscData()
        {
            var data = new RegulatoryChannelData
            {
                Channels6GHz = new Dictionary<int, int[]>
                {
                    [20] = [1, 5, 9, 13, 17, 21]
                },
                PscChannels6GHz = [] // No PSC data
            };

            // Without PSC data, returns all width-valid channels
            var channels = data.GetChannels(RadioBand.Band6GHz, 20);
            channels.Should().Equal(1, 5, 9, 13, 17, 21);
        }
    }

    public class ParseRealApiResponse
    {
        [Fact]
        public void ParsesUsRegulatoryDomain()
        {
            // Simplified but representative US regulatory data
            var json = """
            {
                "key": "US",
                "name": "United States",
                "code": "840",
                "channels_ng": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
                "channels_ng_40": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
                "channels_na": [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165],
                "channels_na_40": [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161],
                "channels_na_80": [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161],
                "channels_na_160": [36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128],
                "channels_na_240": [100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144],
                "channels_na_dfs": [52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144],
                "channels_6e": [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 73, 77, 81, 85, 89, 93, 97, 101, 105, 109, 113, 117, 121, 125, 129, 133, 137, 141, 145, 149, 153, 157, 161, 165, 169, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 233],
                "channels_6e_40": [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 73, 77, 81, 85, 89, 93, 97, 101, 105, 109, 113, 117, 121, 125, 129, 133, 137, 141, 145, 149, 153, 157, 161, 165, 169, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229],
                "channels_6e_80": [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 73, 77, 81, 85, 89, 93, 97, 101, 105, 109, 113, 117, 121, 125, 129, 133, 137, 141, 145, 149, 153, 157, 161, 165, 169, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221],
                "channels_6e_160": [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 73, 77, 81, 85, 89, 93, 97, 101, 105, 109, 113, 117, 121, 125, 129, 133, 137, 141, 145, 149, 153, 157, 161, 165, 169, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221],
                "channels_6e_320": [1, 5, 9, 13, 17, 21, 25, 29, 33, 37, 41, 45, 49, 53, 57, 61, 65, 69, 73, 77, 81, 85, 89, 93, 97, 101, 105, 109, 113, 117, 121, 125, 129, 133, 137, 141, 145, 149, 153, 157, 161, 165, 169, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221],
                "channels_6e_psc": [5, 21, 37, 53, 69, 85, 101, 117, 133, 149, 165, 181, 197, 213, 229]
            }
            """;

            using var doc = JsonDocument.Parse(json);
            var result = RegulatoryChannelData.Parse(doc.RootElement);

            result.Should().NotBeNull();

            // 2.4 GHz: US has 11 channels
            result!.Channels2_4GHz[20].Should().HaveCount(11);

            // 5 GHz at 20 MHz: all 25 channels
            result.Channels5GHz[20].Should().HaveCount(25);

            // 5 GHz at 160 MHz: only UNII-1/2 and UNII-2e (36-128)
            result.Channels5GHz[160].Should().HaveCount(16);
            result.Channels5GHz[160].Should().NotContain(149);

            // 5 GHz at 240 MHz: UNII-2e only (100-144)
            result.Channels5GHz[240].Should().HaveCount(12);
            result.Channels5GHz[240].Should().NotContain(36);

            // DFS channels
            result.DfsChannels.Should().HaveCount(16);
            result.DfsChannels.Should().Contain(52);
            result.DfsChannels.Should().NotContain(36);
            result.DfsChannels.Should().NotContain(149);

            // 5 GHz without DFS at 160 MHz: only 36-48 (UNII-1)
            var noDfs160 = result.GetChannels(RadioBand.Band5GHz, 160, includeDfs: false);
            noDfs160.Should().OnlyContain(ch => ch >= 36 && ch <= 48);

            // PSC channels parsed
            result.PscChannels6GHz.Should().HaveCount(15);
            result.PscChannels6GHz.Should().Contain(5);
            result.PscChannels6GHz.Should().Contain(229);

            // 6 GHz at 320 MHz: returns PSC channels that are valid at 320 MHz
            // 320 MHz list goes up to 221, PSC has 229 - so 229 excluded
            var sixGhz320 = result.GetChannels(RadioBand.Band6GHz, 320);
            sixGhz320.Should().Contain(5);
            sixGhz320.Should().Contain(213);
            sixGhz320.Should().NotContain(229); // Not valid at 320 MHz
            sixGhz320.Should().NotContain(1); // Not PSC
            sixGhz320.Should().NotContain(9); // Not PSC

            // 6 GHz has no DFS filtering
            var sixGhz160 = result.GetChannels(RadioBand.Band6GHz, 160, includeDfs: false);
            sixGhz160.Should().Equal(result.GetChannels(RadioBand.Band6GHz, 160, includeDfs: true));
        }
    }
}
