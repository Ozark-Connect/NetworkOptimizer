using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Data;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ChannelRecommendationWidthTests
{
    private readonly ChannelRecommendationService _service;

    public ChannelRecommendationWidthTests()
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
            { 40, new[] { 36, 44, 52, 60, 100, 108, 116, 124, 132, 140, 149, 157 } },
            { 80, new[] { 36, 52, 100, 116, 132, 149 } },
            { 160, new[] { 36, 100 } }
        },
        DfsChannels = new[] { 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140, 144 }
    };

    private static AccessPointSnapshot Ap(int channel, int width, bool backhaul = false) => new()
    {
        Mac = "aa:bb:cc:dd:ee:01",
        Name = "AP-1",
        Status = new(DeviceStatusKind.Online, "Online"),
        IsMeshChild = backhaul,
        MeshUplinkBand = backhaul ? RadioBand.Band5GHz : null,
        MeshUplinkChannel = backhaul ? channel : null,
        Radios = new() { new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = channel, ChannelWidth = width, HasDfs = true, TxPower = 20 } }
    };

    private static RadioWidthEvidence Evidence(int clients, int negotiated, int supported, int? busy) => new()
    {
        ClientCount = clients, MaxNegotiatedWidth = negotiated, MaxSupportedWidth = supported, MeasuredUtilization = busy
    };

    private static readonly RecommendationOptions Widths = new() { OptimizeWidths = true };

    [Fact]
    public void Without_evidence_the_width_is_not_a_candidate()
    {
        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths);

        graph.Nodes[0].ValidWidths.Should().Equal(80);
        graph.Nodes[0].ValidChannelsByWidth.Should().BeEmpty();
    }

    [Fact]
    public void Evidence_is_ignored_unless_widths_were_asked_for()
    {
        var evidence = new Dictionary<string, RadioWidthEvidence> { ["aa:bb:cc:dd:ee:01"] = Evidence(4, 40, 40, 10) };

        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory,
            new RecommendationOptions(), widthEvidence: evidence);

        graph.Nodes[0].ValidWidths.Should().Equal(80);
    }

    [Fact]
    public void Clients_that_all_negotiate_half_the_width_open_narrower_candidates_with_their_own_channels()
    {
        var evidence = new Dictionary<string, RadioWidthEvidence> { ["aa:bb:cc:dd:ee:01"] = Evidence(4, 40, 40, 30) };

        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths, widthEvidence: evidence);

        graph.Nodes[0].ValidWidths.Should().Equal(40, 80);
        graph.Nodes[0].ValidChannelsByWidth[40].Should().Contain(36).And.Contain(149);
        graph.Nodes[0].ValidChannels.Should().NotContain(157, "the current width keeps its own 80 MHz channel set");
    }

    [Fact]
    public void Capable_clients_and_quiet_air_open_a_wider_candidate()
    {
        var evidence = new Dictionary<string, RadioWidthEvidence> { ["aa:bb:cc:dd:ee:01"] = Evidence(2, 80, 160, 9) };

        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths, widthEvidence: evidence);

        graph.Nodes[0].ValidWidths.Should().Equal(80, 160);
        graph.Nodes[0].ValidChannelsByWidth[160].Should().Equal(36, 100);
    }

    [Theory]
    [InlineData(2, 40, 40, 10)]   // two clients are not a population
    [InlineData(4, 80, 160, 60)]  // busy air: no widening
    [InlineData(4, 80, 80, 5)]    // nothing to gain either way
    public void Thin_or_contrary_evidence_keeps_the_width(int clients, int negotiated, int supported, int busy)
    {
        var evidence = new Dictionary<string, RadioWidthEvidence> { ["aa:bb:cc:dd:ee:01"] = Evidence(clients, negotiated, supported, busy) };

        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths, widthEvidence: evidence);

        graph.Nodes[0].ValidWidths.Should().Equal(80);
    }

    [Fact]
    public void A_backhaul_radio_keeps_its_width_whatever_the_clients_say()
    {
        var evidence = new Dictionary<string, RadioWidthEvidence>
        {
            ["aa:bb:cc:dd:ee:01"] = new() { ClientCount = 5, MaxNegotiatedWidth = 40, MaxSupportedWidth = 40, MeasuredUtilization = 5, CarriesBackhaul = true }
        };

        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80, backhaul: true) }, RadioBand.Band5GHz, null, null, Regulatory, Widths, widthEvidence: evidence);

        graph.Nodes[0].ValidWidths.Should().Equal(80);
    }

    [Fact]
    public void A_width_below_what_clients_can_use_costs_score_and_only_with_evidence()
    {
        var evidence = new Dictionary<string, RadioWidthEvidence> { ["aa:bb:cc:dd:ee:01"] = Evidence(2, 80, 160, 9) };
        var withEvidence = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths, widthEvidence: evidence);
        var without = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths);

        var narrow = _service.ScoreAssignment(withEvidence, new[] { (36, 80) }, RadioBand.Band5GHz);
        var wide = _service.ScoreAssignment(withEvidence, new[] { (36, 160) }, RadioBand.Band5GHz);
        var narrowWithout = _service.ScoreAssignment(without, new[] { (36, 80) }, RadioBand.Band5GHz);
        var wideWithout = _service.ScoreAssignment(without, new[] { (36, 160) }, RadioBand.Band5GHz);

        (narrow - wide).Should().BeApproximately(0.8, 0.001, "one halving below demand costs one unit of the weight");
        narrowWithout.Should().Be(wideWithout, "a console-only radio scores the same at any width");
    }

    [Fact]
    public void A_quiet_radio_with_capable_clients_is_recommended_wider_with_the_reason()
    {
        var evidence = new Dictionary<string, RadioWidthEvidence> { ["aa:bb:cc:dd:ee:01"] = Evidence(2, 80, 160, 9) };
        var graph = _service.BuildInterferenceGraph(new() { Ap(36, 80) }, RadioBand.Band5GHz, null, null, Regulatory, Widths, widthEvidence: evidence);

        var plan = _service.Optimize(graph, RadioBand.Band5GHz, Regulatory, Widths);

        var rec = plan.Recommendations.Single();
        rec.RecommendedChannel.Should().Be(36);
        rec.RecommendedWidth.Should().Be(160);
        rec.WidthReason.Should().Be("Wider because its clients can use 160 MHz and the air is quiet (9% busy).");
    }
}
