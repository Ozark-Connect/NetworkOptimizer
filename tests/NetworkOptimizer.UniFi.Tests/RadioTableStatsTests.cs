using System.Text.Json;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class RadioTableStatsTests
{
    [Fact]
    public void Parses_operating_width_from_bw()
    {
        // radio_table_stats reports the live operating width in "bw". A mesh backhaul radio
        // configured for 160 MHz can negotiate down to the parent's 80 MHz - "bw" reflects the
        // real 80 while radio_table's "ht" still reads 160 (issue #921).
        const string json = """
        {
            "name": "wifi1",
            "radio": "na",
            "channel": 36,
            "bw": 80,
            "extchannel": -1
        }
        """;

        var stats = JsonSerializer.Deserialize<RadioTableStats>(json);

        Assert.NotNull(stats);
        Assert.Equal(36, stats!.Channel);
        Assert.Equal(80, stats.Bw);
    }

    [Theory]
    [InlineData("80", 80)]
    [InlineData("\"160\"", 160)]
    [InlineData("20", 20)]
    public void Parses_bw_as_number_or_string(string bwJson, int expected)
    {
        var json = $"{{\"name\":\"wifi1\",\"radio\":\"na\",\"bw\":{bwJson}}}";

        var stats = JsonSerializer.Deserialize<RadioTableStats>(json);

        Assert.Equal(expected, stats!.Bw);
    }

    [Fact]
    public void Bw_is_null_when_absent()
    {
        const string json = """{"name":"wifi1","radio":"na","channel":36}""";

        var stats = JsonSerializer.Deserialize<RadioTableStats>(json);

        Assert.Null(stats!.Bw);
    }

    [Fact]
    public void Parses_real_radio_table_stats_array()
    {
        // Captured radio_table_stats from a real device (issue #921): the 5 GHz radio operates at
        // 80 MHz (bw) even though it's configured for a wider channel, which is the whole point of
        // reading bw over radio_table's ht.
        const string json = """
        [
            { "name": "wifi0", "radio": "ng", "channel": 6,  "bw": 20, "state": "RUN", "extchannel": 0 },
            { "name": "wifi1", "radio": "na", "channel": 36, "bw": 80, "state": "RUN", "extchannel": 1 }
        ]
        """;

        var stats = JsonSerializer.Deserialize<List<RadioTableStats>>(json);

        Assert.NotNull(stats);
        Assert.Equal(2, stats!.Count);

        var ng = stats.Single(r => r.Radio == "ng");
        Assert.Equal(6, ng.Channel);
        Assert.Equal(20, ng.Bw);

        var na = stats.Single(r => r.Radio == "na");
        Assert.Equal(36, na.Channel);
        Assert.Equal(80, na.Bw);
    }
}
