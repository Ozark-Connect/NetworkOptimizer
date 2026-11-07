using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ChannelRecommendationQuietestChannelTests
{
    private readonly ChannelRecommendationService _service = new(
        new PropagationService(new AntennaPatternLoader(NullLogger<AntennaPatternLoader>.Instance), NullLogger<PropagationService>.Instance),
        NullLogger<ChannelRecommendationService>.Instance);

    private static ChannelScanResult Scan(RadioBand band, params (int Channel, int Utilization)[] channels) => new()
    {
        ApMac = "aa:bb:cc:dd:ee:01", Band = band,
        Channels = channels.Select(c => new ChannelInfo { Channel = c.Channel, Width = 20, Utilization = c.Utilization }).ToList()
    };

    [Fact]
    public void On_24_GHz_only_1_6_and_11_are_candidates()
    {
        var radio = new RadioSnapshot { Band = RadioBand.Band2_4GHz, Channel = 1, ChannelWidth = 20 };
        var scan = Scan(RadioBand.Band2_4GHz, (1, 60), (3, 5), (6, 30), (11, 45));

        _service.QuietestCandidateUtilization(RadioBand.Band2_4GHz, radio, scan, null, DfsPreference.IncludeWithPenalty)
            .Should().Be(30, "channel 3 overlaps 1 and 6 and is never recommended");
    }

    [Fact]
    public void A_wide_radio_reads_each_candidate_over_its_span()
    {
        var radio = new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36, ChannelWidth = 40 };
        var scan = Scan(RadioBand.Band5GHz, (36, 10), (40, 50), (149, 20), (153, 20));

        // 36/40 averages 30; 149/153 averages 20.
        _service.QuietestCandidateUtilization(RadioBand.Band5GHz, radio, scan, null, DfsPreference.IncludeWithPenalty)
            .Should().Be(20);
    }

    [Fact]
    public void Without_a_scan_there_is_no_quietest_channel()
    {
        var radio = new RadioSnapshot { Band = RadioBand.Band2_4GHz, Channel = 6, ChannelWidth = 20 };
        _service.QuietestCandidateUtilization(RadioBand.Band2_4GHz, radio, null, null, DfsPreference.IncludeWithPenalty).Should().BeNull();
        _service.QuietestCandidateUtilization(RadioBand.Band2_4GHz, radio, Scan(RadioBand.Band2_4GHz), null, DfsPreference.IncludeWithPenalty).Should().BeNull();
    }
}
