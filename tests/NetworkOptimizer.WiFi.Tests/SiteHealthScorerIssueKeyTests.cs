using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Analyzers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class SiteHealthScorerIssueKeyTests
{
    private static AccessPointSnapshot Ap(string mac, string name) => new()
    {
        Mac = mac, Name = name, Status = new(DeviceStatusKind.Online, "Online"),
        Radios = new() { new RadioSnapshot { Band = RadioBand.Band2_4GHz, Channel = 6, ChannelUtilization = 20 } }
    };

    private static WirelessClientSnapshot Client(string mac, string apMac, string apName, int signal) => new()
    {
        Mac = mac, Name = mac, ApMac = apMac, ApName = apName, Band = RadioBand.Band2_4GHz, Signal = signal, WifiProtocol = "ax"
    };

    [Fact]
    public void A_weak_client_keeps_one_key_whichever_ap_it_roams_to()
    {
        var scorer = new SiteHealthScorer();
        var aps = new List<AccessPointSnapshot> { Ap("aa:bb:cc:dd:ee:01", "AP-1"), Ap("aa:bb:cc:dd:ee:02", "AP-2") };

        var onAp1 = scorer.Calculate(aps, new List<WirelessClientSnapshot> { Client("cc:00:00:00:00:01", aps[0].Mac, "AP-1", -82) }, null);
        var onAp2 = scorer.Calculate(aps, new List<WirelessClientSnapshot> { Client("cc:00:00:00:00:01", aps[1].Mac, "AP-2", -80) }, null);

        var key1 = onAp1.Issues.Single(i => i.Title == "Weak signal").Key;
        var key2 = onAp2.Issues.Single(i => i.Title == "Weak signal").Key;
        key1.Should().Be("WIFI-WEAK-SIGNAL-001|cc:00:00:00:00:01");
        key2.Should().Be(key1);
    }

    [Fact]
    public void Two_weak_clients_are_two_issues_with_their_own_keys()
    {
        var scorer = new SiteHealthScorer();
        var aps = new List<AccessPointSnapshot> { Ap("aa:bb:cc:dd:ee:01", "AP-1") };
        var clients = new List<WirelessClientSnapshot>
        {
            Client("cc:00:00:00:00:01", aps[0].Mac, "AP-1", -82),
            Client("cc:00:00:00:00:02", aps[0].Mac, "AP-1", -79),
        };

        var score = scorer.Calculate(aps, clients, null);

        score.Issues.Where(i => i.Title == "Weak signal").Select(i => i.Key).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        score.Issues.Should().OnlyContain(i => !string.IsNullOrEmpty(i.Key), "every issue must be acknowledgeable");
    }
}
