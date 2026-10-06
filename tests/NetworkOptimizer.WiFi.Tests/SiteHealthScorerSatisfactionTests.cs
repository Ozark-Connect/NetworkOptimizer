using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>UniFi reports satisfaction -1 for an AP with no clients: no score, never a score of -1.</summary>
public class SiteHealthScorerSatisfactionTests
{
    private static AccessPointSnapshot Ap(string mac, int? satisfaction) => new()
    {
        Mac = mac, Name = mac, Status = new(DeviceStatusKind.Online, "Online"), Satisfaction = satisfaction,
        Radios = new() { new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36, ChannelUtilization = 10 } }
    };

    [Fact]
    public void No_clients_and_every_ap_at_minus_one_is_no_data()
    {
        var aps = new List<AccessPointSnapshot> { Ap("aa:bb:cc:dd:ee:01", -1), Ap("aa:bb:cc:dd:ee:02", -1) };

        var score = new SiteHealthScorer().Calculate(aps, new List<WirelessClientSnapshot>(), null);

        score.ClientSatisfaction.Score.Should().Be(80);
        score.ClientSatisfaction.Status.Should().Be("No satisfaction data available");
    }

    [Fact]
    public void An_ap_at_minus_one_does_not_drag_down_one_with_a_score()
    {
        var aps = new List<AccessPointSnapshot> { Ap("aa:bb:cc:dd:ee:01", -1), Ap("aa:bb:cc:dd:ee:02", 90) };

        var score = new SiteHealthScorer().Calculate(aps, new List<WirelessClientSnapshot>(), null);

        score.ClientSatisfaction.Score.Should().Be(90);
    }
}
