using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>
/// A pinned radio must survive every pass of the engine, not only the search: the per-AP
/// fallback and the altruistic pass both move radios the search left alone.
/// </summary>
public class ChannelRecommendationPinTests
{
    private readonly ChannelRecommendationService _service;

    public ChannelRecommendationPinTests()
    {
        var loader = new AntennaPatternLoader(NullLogger<AntennaPatternLoader>.Instance);
        var propagation = new PropagationService(loader, NullLogger<PropagationService>.Instance);
        _service = new ChannelRecommendationService(propagation, NullLogger<ChannelRecommendationService>.Instance);
    }

    private static readonly RegulatoryChannelData Regulatory = new()
    {
        Channels5GHz = new Dictionary<int, int[]>
        {
            { 20, new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144, 149, 153, 157, 161, 165 } },
            { 80, new[] { 36, 52, 100, 116, 132, 149 } }
        },
        DfsChannels = new[] { 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144 }
    };

    private static AccessPointSnapshot Ap(string mac, int channel) => new()
    {
        Mac = mac, Name = mac, Status = new(DeviceStatusKind.Online, "Online"),
        Radios = new() { new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = channel, ChannelWidth = 80, HasDfs = true, TxPower = 20, AntennaGain = 3 } }
    };

    /// <summary>One AP on a jammed channel with a clean, directly scanned alternative: the shape that moves.</summary>
    private InterferenceGraph JammedGraph(RecommendationOptions options)
    {
        var graph = _service.BuildInterferenceGraph(new() { Ap("aa:bb:cc:dd:ee:01", 52) }, RadioBand.Band5GHz, null, null, Regulatory, options);
        graph.ExternalLoad[0] = new() { { 52, 5.0 }, { 36, 0.0 } };
        graph.DirectlyObservedChannels[0] = new() { 52, 36 };
        return graph;
    }

    [Fact]
    public void Unpinned_the_jammed_radio_moves()
    {
        var options = new RecommendationOptions();

        var plan = _service.Optimize(JammedGraph(options), RadioBand.Band5GHz, Regulatory, options);

        plan.Recommendations.Single().RecommendedChannel.Should().Be(36);
    }

    [Fact]
    public void Pinned_the_jammed_radio_stays_through_every_pass()
    {
        var options = new RecommendationOptions { PinnedApMacs = new HashSet<string> { "aa:bb:cc:dd:ee:01" } };

        var plan = _service.Optimize(JammedGraph(options), RadioBand.Band5GHz, Regulatory, options);

        var rec = plan.Recommendations.Single();
        rec.RecommendedChannel.Should().Be(52, "the pin holds it however badly it scores");
        rec.IsChanged.Should().BeFalse();
    }
}
