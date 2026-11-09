using FluentAssertions;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class SiteHealthScorerChannelHealthTests
{
    private static RadioSnapshot Radio(double utilization, int clients, int? quietest) => new()
    {
        Band = RadioBand.Band2_4GHz, Channel = 6, ChannelWidth = 20,
        ChannelUtilization = (int)utilization, HourUtilizationAvg = utilization,
        ClientCount = clients, QuietestChannelUtilization = quietest
    };

    private static ScoreDimension ChannelHealth(params RadioSnapshot[] radios)
    {
        var aps = radios.Select((r, i) => new AccessPointSnapshot
        {
            Mac = $"aa:bb:cc:dd:ee:0{i + 1}", Name = $"AP-{i + 1}", Radios = [r]
        }).ToList();
        return new SiteHealthScorer().Calculate(aps, []).ChannelHealth;
    }

    [Fact]
    public void A_band_busy_everywhere_costs_nothing()
    {
        var health = ChannelHealth(Radio(80, 6, quietest: 80), Radio(85, 4, quietest: 85));

        health.Score.Should().Be(100, "no channel the radios could use is any quieter");
        var factor = health.Factors.Should().ContainSingle(f => f.Name == "Utilization above quietest channel").Subject;
        factor.Value.Should().Be("+0%");
    }

    [Fact]
    public void A_radio_on_a_worse_channel_costs_the_gap()
    {
        ChannelHealth(Radio(80, 5, quietest: 50)).Score.Should().Be(70);
    }

    [Fact]
    public void The_gap_is_weighted_by_clients_and_an_empty_radio_weighs_nothing()
    {
        // 30 points of gap on 3 clients and none on 1 client: 22.5 client-weighted.
        ChannelHealth(Radio(80, 3, quietest: 50), Radio(60, 1, quietest: 60)).Score.Should().Be(77);
        ChannelHealth(Radio(95, 0, quietest: 10), Radio(60, 5, quietest: 60)).Score.Should().Be(100);
    }

    [Fact]
    public void Without_a_scan_the_radio_is_scored_above_the_old_floor()
    {
        ChannelHealth(Radio(70, 4, quietest: null)).Score.Should().Be(80);
        ChannelHealth(Radio(45, 4, quietest: null)).Score.Should().Be(100);
    }
}
