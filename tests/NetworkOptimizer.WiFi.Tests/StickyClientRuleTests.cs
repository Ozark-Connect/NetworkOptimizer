using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class StickyClientRuleTests
{
    private readonly StickyClientRule _rule = new();

    private static AccessPointSnapshot Ap(string mac, string name, params RadioBand[] bands) => new()
    {
        Mac = mac,
        Name = name,
        Status = new(DeviceStatusKind.Online, "Online"),
        Radios = bands.Select(b => new RadioSnapshot { Band = b, Channel = 36 }).ToList()
    };

    private static WirelessClientSnapshot Client(string apMac, string name, int join, int now, double hours,
        int? nudges = null, int? accepted = null) => new()
    {
        Mac = $"cc:cc:cc:00:00:{name.Length:D2}",
        Name = name,
        ApMac = apMac,
        Band = RadioBand.Band5GHz,
        Signal = now,
        JoinSignal = join,
        AssociatedFor = TimeSpan.FromHours(hours),
        RoamNudges = nudges,
        RoamNudgesAccepted = accepted
    };

    private static WiFiOptimizerContext Context(List<AccessPointSnapshot> aps, List<WirelessClientSnapshot> clients) => new()
    {
        AccessPoints = aps, Clients = clients, Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    private static readonly List<AccessPointSnapshot> TwoAps =
    [
        Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz),
        Ap("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band5GHz)
    ];

    [Fact]
    public void Two_clients_that_joined_weak_and_stayed_weak_for_hours_are_one_issue()
    {
        var clients = new List<WirelessClientSnapshot>
        {
            Client("aa:bb:cc:dd:ee:01", "Tablet", -80, -81, 3),
            Client("aa:bb:cc:dd:ee:01", "Cam", -82, -79, 3.5, nudges: 2, accepted: 0),
        };

        var issue = _rule.EvaluateAll(Context(TwoAps, clients)).Single();

        issue.Title.Should().Be("Sticky Clients on AP-1");
        issue.Class.Should().Be(HealthIssueClass.Measured);
        issue.Key.Should().Be("WIFI-STICKY-CLIENT-001|aa:bb:cc:dd:ee:01");
        issue.Description.Should().Be(
            "2 client(s) joined AP-1 at a weak signal and have stayed for over 3 hour(s) without roaming: " +
            "Tablet (joined at -80 dBm, now -81 dBm), Cam (joined at -82 dBm, now -79 dBm). " +
            "1 of them ignored a roam nudge (BSS transition request).");
        issue.Recommendation.Should().Contain("set Minimum RSSI on AP-1");
    }

    [Fact]
    public void A_client_that_arrived_an_hour_ago_is_not_sticky_yet()
    {
        var clients = new List<WirelessClientSnapshot>
        {
            Client("aa:bb:cc:dd:ee:01", "Tablet", -80, -81, 3),
            Client("aa:bb:cc:dd:ee:01", "Cam", -80, -79, 1),
        };

        _rule.EvaluateAll(Context(TwoAps, clients)).Should().BeEmpty();
    }

    [Fact]
    public void Nowhere_to_go_is_not_sticky()
    {
        var oneAp = new List<AccessPointSnapshot> { Ap("aa:bb:cc:dd:ee:01", "AP-1", RadioBand.Band5GHz), Ap("aa:bb:cc:dd:ee:02", "AP-2", RadioBand.Band2_4GHz) };
        var clients = new List<WirelessClientSnapshot>
        {
            Client("aa:bb:cc:dd:ee:01", "Tablet", -80, -81, 3),
            Client("aa:bb:cc:dd:ee:01", "Cam", -80, -79, 3),
        };

        _rule.EvaluateAll(Context(oneAp, clients)).Should().BeEmpty();
    }

    [Fact]
    public void Console_clients_carry_no_join_signal_and_never_qualify()
    {
        var clients = new List<WirelessClientSnapshot>
        {
            Client("aa:bb:cc:dd:ee:01", "Tablet", -80, -81, 3),
            Client("aa:bb:cc:dd:ee:01", "Cam", -80, -79, 3),
        };
        foreach (var c in clients) { c.JoinSignal = null; c.AssociatedFor = null; }

        _rule.EvaluateAll(Context(TwoAps, clients)).Should().BeEmpty();
    }
}
