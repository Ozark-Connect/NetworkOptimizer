using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Rules;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class LatencyDespiteSignalRuleTests
{
    private readonly LatencyDespiteSignalRule _rule = new();
    private const string ApMac = "aa:bb:cc:dd:ee:01";

    private static AccessPointSnapshot Ap() => new()
    {
        Mac = ApMac,
        Name = "AP-1",
        Status = new(DeviceStatusKind.Online, "Online"),
        Radios = new() { new RadioSnapshot { Band = RadioBand.Band5GHz, Channel = 36 } }
    };

    private static WirelessClientSnapshot Client(int index, int signal, double latency, int stalls) => new()
    {
        Mac = $"cc:cc:cc:00:00:{index:D2}",
        Name = $"Client-{index}",
        ApMac = ApMac,
        Band = RadioBand.Band5GHz,
        Signal = signal,
        MeasuredLatencyAvgMs = latency,
        MeasuredTcpStalls = stalls
    };

    private static WiFiOptimizerContext Context(params WirelessClientSnapshot[] clients) => new()
    {
        AccessPoints = [Ap()], Clients = clients.ToList(), Wlans = [], Networks = [], LegacyClients = [], SteerableClients = []
    };

    [Fact]
    public void Good_signal_and_high_latency_is_the_issue()
    {
        var issue = _rule.EvaluateAll(Context(
            Client(1, -55, 80, 10), Client(2, -58, 85, 12), Client(3, -60, 90, 15), Client(4, -62, 40, 0))).Single();

        issue.Title.Should().Be("High Latency Despite Good Signal on AP-1");
        issue.Key.Should().Be("WIFI-LATENCY-001|aa:bb:cc:dd:ee:01/na");
        issue.Class.Should().Be(HealthIssueClass.Measured);
        issue.Description.Should().Be(
            "4 client(s) on AP-1's 5 GHz radio have strong signal but slow service: " +
            "a median transmit latency of 82 ms at the AP and 37 TCP stalls between them in the last hour. " +
            "Signal is not the problem; airtime is.");
    }

    [Fact]
    public void Stalls_alone_are_enough()
    {
        _rule.EvaluateAll(Context(Client(1, -55, 10, 10), Client(2, -58, 12, 8), Client(3, -60, 9, 5)))
            .Should().ContainSingle();
    }

    [Fact]
    public void A_weak_population_is_the_weak_signal_rules_problem()
    {
        _rule.EvaluateAll(Context(Client(1, -78, 80, 10), Client(2, -80, 85, 12), Client(3, -79, 90, 15)))
            .Should().BeEmpty();
    }

    [Fact]
    public void Two_covered_clients_are_not_a_median()
    {
        _rule.EvaluateAll(Context(Client(1, -55, 80, 10), Client(2, -58, 85, 12))).Should().BeEmpty();
    }

    [Fact]
    public void Console_clients_carry_no_latency_and_never_qualify()
    {
        var clients = new[] { Client(1, -55, 80, 10), Client(2, -58, 85, 12), Client(3, -60, 90, 15) };
        foreach (var c in clients) c.MeasuredLatencyAvgMs = null;

        _rule.EvaluateAll(Context(clients)).Should().BeEmpty();
    }
}
