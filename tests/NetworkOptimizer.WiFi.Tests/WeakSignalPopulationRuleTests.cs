using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class WeakSignalPopulationRuleTests
{
    private readonly WeakSignalPopulationRule _rule = new();

    private static WiFiOptimizerContext Context(List<AccessPointSnapshot> aps, List<WirelessClientSnapshot> clients) => new()
    {
        AccessPoints = aps, Clients = clients, Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    private static AccessPointSnapshot Ap(string mac, string name) => new() { Mac = mac, Name = name };

    private static WirelessClientSnapshot Client(string apMac, int signal) => new()
    {
        Mac = Guid.NewGuid().ToString("N")[..12], ApMac = apMac, Signal = signal, Band = RadioBand.Band5GHz
    };

    [Fact]
    public void A_small_site_is_not_judged_by_share()
    {
        // 2 weak of 5 (40%): two phones, not a deployment problem.
        var ap = Ap("aa:bb:cc:dd:ee:01", "AP-1");
        var clients = new List<WirelessClientSnapshot>
        {
            Client(ap.Mac, -86), Client(ap.Mac, -80), Client(ap.Mac, -55), Client(ap.Mac, -60), Client(ap.Mac, -50)
        };

        _rule.Evaluate(Context([ap], clients)).Should().BeNull();
    }

    [Fact]
    public void Weak_clients_all_on_a_coverage_gap_ap_are_left_to_that_issue()
    {
        // AP-1: 4 of 5 weak (a coverage gap). AP-2: 5 strong. 4 of 10 weak site-wide, all on AP-1.
        var ap1 = Ap("aa:bb:cc:dd:ee:01", "AP-1");
        var ap2 = Ap("aa:bb:cc:dd:ee:02", "AP-2");
        var clients = new List<WirelessClientSnapshot>
        {
            Client(ap1.Mac, -82), Client(ap1.Mac, -83), Client(ap1.Mac, -84), Client(ap1.Mac, -85), Client(ap1.Mac, -50),
            Client(ap2.Mac, -50), Client(ap2.Mac, -52), Client(ap2.Mac, -54), Client(ap2.Mac, -56), Client(ap2.Mac, -58)
        };

        _rule.Evaluate(Context([ap1, ap2], clients)).Should().BeNull();
    }

    [Fact]
    public void Weak_clients_spread_across_aps_raise_the_issue()
    {
        // 4 of 12 weak, 2 per AP: no AP reaches the coverage gap share, so only this issue speaks.
        var ap1 = Ap("aa:bb:cc:dd:ee:01", "AP-1");
        var ap2 = Ap("aa:bb:cc:dd:ee:02", "AP-2");
        var clients = new List<WirelessClientSnapshot>
        {
            Client(ap1.Mac, -82), Client(ap1.Mac, -83), Client(ap1.Mac, -50), Client(ap1.Mac, -52), Client(ap1.Mac, -54), Client(ap1.Mac, -56),
            Client(ap2.Mac, -84), Client(ap2.Mac, -85), Client(ap2.Mac, -50), Client(ap2.Mac, -52), Client(ap2.Mac, -54), Client(ap2.Mac, -56)
        };

        var issue = _rule.Evaluate(Context([ap1, ap2], clients));

        issue.Should().NotBeNull();
        issue!.Description.Should().StartWith("4 clients (33% of total)");
    }
}
