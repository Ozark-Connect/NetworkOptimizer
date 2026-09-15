using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>The live cache's memory behind wired port presence: unicast movement per port and the console's placements.</summary>
public class MonitoringLiveStatsPortPresenceTests
{
    private const string Switch = "aa:bb:cc:dd:ee:01";
    private static readonly DateTime T0 = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    private static MonitoringLiveStats Stats() => new(
        NullLogger<MonitoringLiveStats>.Instance,
        Mock.Of<IDbContextFactory<NetworkOptimizerDbContext>>());

    private static MonitoringInfluxClient.PortStatsPoint Sample(long? ucastIn, DateTime at) => new()
    {
        DeviceMac = Switch,
        IfName = "eth1",
        UcastPktsIn = ucastIn,
        Time = at,
    };

    [Fact]
    public void Unicast_in_is_stamped_only_once_the_counter_has_moved_between_two_samples()
    {
        var stats = Stats();
        stats.RecordPortStats(Sample(1000, T0));
        stats.GetPortUnicastIn(Switch, "eth1").Should().BeNull();

        stats.RecordPortStats(Sample(1000, T0.AddSeconds(5)));
        stats.GetPortUnicastIn(Switch, "eth1").Should().BeNull();

        stats.RecordPortStats(Sample(1040, T0.AddSeconds(10)));
        var moved = stats.GetPortUnicastIn(Switch, "eth1")!.Value;
        moved.Packets.Should().Be(40);
        moved.At.Should().Be(T0.AddSeconds(10));

        // A quiet sample keeps the last movement rather than erasing it.
        stats.RecordPortStats(Sample(1040, T0.AddSeconds(15)));
        stats.GetPortUnicastIn(Switch, "eth1")!.Value.At.Should().Be(T0.AddSeconds(10));
    }

    private static MonitoringInfluxClient.PortStatsPoint Rated(double? inBps, double? outBps, int oper, DateTime at) => new()
    {
        DeviceMac = Switch,
        IfName = "eth1",
        OperStatus = oper,
        RateInBps = inBps,
        RateOutBps = outBps,
        Time = at,
    };

    private static MonitoringInfluxClient.PortStatsPoint Port(MonitoringLiveStats stats) =>
        stats.GetPortStatsSnapshot(new[] { Switch }).Single();

    [Fact]
    public void A_rate_is_held_for_a_sample_without_one_then_reads_as_idle()
    {
        var stats = Stats();
        stats.RecordPortStats(Rated(1000, 2000, 1, T0));
        stats.RecordPortStats(Rated(null, null, 1, T0.AddSeconds(5)));
        Port(stats).RateInBps.Should().Be(1000);

        stats.RecordPortStats(Rated(null, null, 1, T0 + MonitoringLiveStats.PortRateHold + TimeSpan.FromSeconds(1)));
        Port(stats).RateInBps.Should().Be(0);
        Port(stats).RateOutBps.Should().Be(0);
    }

    [Fact]
    public void Link_state_is_answered_only_from_a_fresh_sample()
    {
        var stats = Stats();
        stats.IsPortLinkDown(Switch, "eth1", T0, TimeSpan.FromSeconds(90)).Should().BeNull();

        stats.RecordPortStats(Rated(null, null, 1, T0));
        stats.IsPortLinkDown(Switch, "eth1", T0.AddSeconds(5), TimeSpan.FromSeconds(90)).Should().BeFalse();

        stats.RecordPortStats(Rated(null, null, 2, T0.AddSeconds(10)));
        stats.IsPortLinkDown(Switch, "eth1", T0.AddSeconds(15), TimeSpan.FromSeconds(90)).Should().BeTrue();
        stats.IsPortLinkDown(Switch, "eth1", T0.AddSeconds(200), TimeSpan.FromSeconds(90)).Should().BeNull();
    }

    [Fact]
    public void A_down_sample_from_before_the_client_connected_is_not_held_against_it()
    {
        var stats = Stats();
        stats.RecordPortStats(Rated(null, null, 2, T0.AddSeconds(10)));
        // The console saw the client connect after the sample: the newer observation wins.
        stats.IsPortLinkDown(Switch, "eth1", T0.AddSeconds(15), TimeSpan.FromSeconds(90), notBefore: T0.AddSeconds(12)).Should().BeNull();
        // Connected before the sample: the sample still counts.
        stats.IsPortLinkDown(Switch, "eth1", T0.AddSeconds(15), TimeSpan.FromSeconds(90), notBefore: T0.AddSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void A_down_link_reads_as_idle_at_once()
    {
        var stats = Stats();
        stats.RecordPortStats(Rated(1000, 2000, 1, T0));
        stats.RecordPortStats(Rated(null, null, 2, T0.AddSeconds(5)));
        Port(stats).RateInBps.Should().Be(0);
        Port(stats).RateOutBps.Should().Be(0);
    }

    [Fact]
    public void A_counter_reset_is_not_movement()
    {
        var stats = Stats();
        stats.RecordPortStats(Sample(5000, T0));
        stats.RecordPortStats(Sample(10, T0.AddSeconds(5)));
        stats.GetPortUnicastIn(Switch, "eth1").Should().BeNull();
    }

    [Fact]
    public void A_placement_keeps_its_newest_sighting_and_every_client_a_port_carried()
    {
        var stats = Stats();
        stats.RecordPortOccupant(Switch, 1, "00:11:22:33:44:55", "192.0.2.10", "Server1", T0);
        stats.RecordPortOccupant(Switch, 1, "00:11:22:33:44:55", "192.0.2.11", "Server1", T0.AddMinutes(1));
        stats.RecordPortOccupant(Switch, 1, "00:11:22:33:44:55", "192.0.2.9", "Server1", T0.AddMinutes(-1));
        stats.RecordPortOccupant(Switch, 1, "00:11:22:33:44:66", "192.0.2.20", "Server2", T0);

        var all = stats.GetPortOccupants();
        all.Should().HaveCount(2);
        all.Single(o => o.ClientMac == "00:11:22:33:44:55").Ip.Should().Be("192.0.2.11");
    }

    [Fact]
    public void Seeding_fills_only_what_the_collector_has_not_seen_and_marks_the_table_seeded()
    {
        var stats = Stats();
        stats.PortOccupantsSeeded.Should().BeFalse();
        stats.RecordPortOccupant(Switch, 1, "00:11:22:33:44:55", "192.0.2.10", "Server1", T0);

        stats.SeedPortOccupants(new[]
        {
            new MonitoringLiveStats.PortOccupant(Switch, 1, "00:11:22:33:44:55", "192.0.2.1", "Old", T0.AddDays(-1)),
            new MonitoringLiveStats.PortOccupant(Switch, 2, "00:11:22:33:44:77", "192.0.2.30", "Server3", T0.AddDays(-2)),
        });

        stats.PortOccupantsSeeded.Should().BeTrue();
        var all = stats.GetPortOccupants();
        all.Should().HaveCount(2);
        all.Single(o => o.Port == 1).Ip.Should().Be("192.0.2.10");
    }
}
