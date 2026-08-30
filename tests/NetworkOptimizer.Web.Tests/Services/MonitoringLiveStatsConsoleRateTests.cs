using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// The live cache's console-rate store behind the Bandwidth Hogs baselines: latest reading with a
/// single-zero hold, plus the rolling history the baselines read.
/// </summary>
public class MonitoringLiveStatsConsoleRateTests
{
    private static MonitoringLiveStats Stats() => new(
        NullLogger<MonitoringLiveStats>.Instance,
        Mock.Of<IDbContextFactory<NetworkOptimizerDbContext>>());

    private static readonly DateTime T0 = new(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_single_zero_reading_is_held_and_a_second_is_accepted()
    {
        var stats = Stats();
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 100, 50, T0);
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 0, 0, T0.AddSeconds(30));

        var held = stats.GetConsoleWanRate("aa:bb:cc:dd:ee:ff", TimeSpan.FromDays(1))!.Value;
        held.DownBps.Should().Be(100);
        held.UpBps.Should().Be(50);

        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 0, 0, T0.AddSeconds(60));
        stats.GetConsoleWanRate("aa:bb:cc:dd:ee:ff", TimeSpan.FromDays(1))!.Value.DownBps.Should().Be(0);
    }

    [Fact]
    public void History_keeps_what_was_recorded_and_drops_what_aged_out()
    {
        var stats = Stats();
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 10, 1, T0.AddMinutes(-20));
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 20, 2, T0.AddMinutes(-10));
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 30, 3, T0);

        var history = stats.ConsoleRateHistory("aa:bb:cc:dd:ee:ff");
        history.Should().HaveCount(2);
        history.Select(h => h.Down).Should().Equal(20, 30);
    }

    [Fact]
    public void History_is_per_client_and_mac_case_insensitive()
    {
        var stats = Stats();
        stats.RecordConsoleWanRate("AA:BB:CC:DD:EE:FF", 10, 1, T0);
        stats.RecordConsoleWanRate("11:22:33:44:55:66", 99, 9, T0);

        stats.ConsoleRateHistory("aa:bb:cc:dd:ee:ff").Should().HaveCount(1);
        stats.ConsoleRateHistory("aa:bb:cc:dd:ee:ff")[0].Down.Should().Be(10);
        stats.ConsoleRateHistory("00:00:00:00:00:01").Should().BeEmpty();
    }

    [Fact]
    public void A_held_zero_lands_in_history_as_the_held_value()
    {
        var stats = Stats();
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 100, 50, T0);
        stats.RecordConsoleWanRate("aa:bb:cc:dd:ee:ff", 0, 0, T0.AddSeconds(30));

        stats.ConsoleRateHistory("aa:bb:cc:dd:ee:ff").Select(h => h.Down).Should().Equal(100, 100);
    }

    [Fact]
    public void Port_rate_writes_feed_the_row_history_with_pruning_and_decimation()
    {
        var stats = Stats();
        stats.RecordPortRate("AA:BB:CC:DD:EE:01", "eth1", 10, 1, T0.AddMinutes(-20));
        stats.RecordPortRate("AA:BB:CC:DD:EE:01", "eth1", 20, 2, T0.AddMinutes(-10));
        stats.RecordPortRate("AA:BB:CC:DD:EE:01", "eth1", 99, 9, T0.AddMinutes(-10).AddSeconds(5)); // inside the spacing, dropped
        stats.RecordPortRate("AA:BB:CC:DD:EE:01", "eth1", 30, 3, T0);

        var history = stats.RowRateHistory(MonitoringLiveStats.PortRowKey("aa:bb:cc:dd:ee:01", "eth1"));
        history.Select(s => s.Down).Should().Equal(20, 30);
    }

    [Fact]
    public void Wired_client_writes_feed_their_own_row_history()
    {
        var stats = Stats();
        stats.RecordWiredClient(new WiredClientLiveSnapshot { ClientMac = "AA:BB:CC:DD:EE:02", TxThroughputBps = 5, RxThroughputBps = 1, LastUpdate = T0.AddMinutes(-1) });
        stats.RecordWiredClient(new WiredClientLiveSnapshot { ClientMac = "aa:bb:cc:dd:ee:02", TxThroughputBps = 7, RxThroughputBps = 2, LastUpdate = T0 });

        var history = stats.RowRateHistory(MonitoringLiveStats.WiredRowKey("aa:bb:cc:dd:ee:02"));
        history.Select(s => s.Down).Should().Equal(5, 7);
        stats.RowRateHistory(MonitoringLiveStats.WifiRowKey("aa:bb:cc:dd:ee:02")).Should().BeEmpty();
    }
}
