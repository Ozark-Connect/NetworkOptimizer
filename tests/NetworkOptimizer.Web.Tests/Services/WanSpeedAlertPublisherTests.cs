using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Tests.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>A WAN test marked Invalid for background load neither raises a degradation alert nor sets the baseline.</summary>
public class WanSpeedAlertPublisherTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly CapturingBus _bus = new();

    private Task<NetworkOptimizerDbContext> OpenDb() => Task.FromResult(new NetworkOptimizerDbContext(
        new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseInMemoryDatabase(_dbName).Options));

    private static Iperf3Result Result(int minutesAgo, double downMbps, double upMbps = 100, bool invalid = false) => new()
    {
        Direction = SpeedTestDirection.UwnWan,
        WanName = "Fiber",
        WanNetworkGroup = "WAN",
        TestTime = T0.AddMinutes(-minutesAgo),
        DownloadBitsPerSecond = downMbps * 1_000_000,
        UploadBitsPerSecond = upMbps * 1_000_000,
        Success = true,
        BackgroundLoadInvalid = invalid,
    };

    private async Task<Iperf3Result> Store(Iperf3Result result)
    {
        await using var db = await OpenDb();
        db.Iperf3Results.Add(result);
        await db.SaveChangesAsync();
        return result;
    }

    private async Task Baseline(params Iperf3Result[] results)
    {
        foreach (var r in results) await Store(r);
    }

    private IEnumerable<string> Published => _bus.Published.Select(e => e.EventType);

    [Fact]
    public async Task A_valid_drop_below_the_recent_average_raises_degradation()
    {
        await Baseline(Result(30, 1000), Result(20, 1000), Result(10, 1000));
        var current = await Store(Result(0, 500));

        await WanSpeedAlertPublisher.PublishDegradationAsync(_bus, current, OpenDb, NullLogger.Instance);

        Published.Should().Equal("wan.speed_degradation");
    }

    [Fact]
    public async Task A_test_marked_invalid_raises_no_degradation()
    {
        await Baseline(Result(30, 1000), Result(20, 1000), Result(10, 1000));
        var current = await Store(Result(0, 500, invalid: true));

        await WanSpeedAlertPublisher.PublishDegradationAsync(_bus, current, OpenDb, NullLogger.Instance);

        Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_results_do_not_drag_the_baseline_down()
    {
        // With the two Invalid 200 Mbps tests in the window the average is 680 and 950 is no drop.
        await Baseline(Result(50, 1000), Result(40, 1000), Result(30, 1000),
            Result(20, 200, invalid: true), Result(10, 200, invalid: true));
        var current = await Store(Result(0, 950));

        await WanSpeedAlertPublisher.PublishDegradationAsync(_bus, current, OpenDb, NullLogger.Instance);

        _bus.Published.Should().ContainSingle(e => e.EventType == "wan.speed_degradation")
            .Which.ThresholdValue.Should().BeApproximately(1000, 0.01);
    }

    [Fact]
    public async Task Deferring_degradation_still_publishes_the_completed_event()
    {
        await Baseline(Result(30, 1000), Result(20, 1000), Result(10, 1000));
        var current = await Store(Result(0, 500));

        await WanSpeedAlertPublisher.PublishAsync(_bus, current, OpenDb, NullLogger.Instance, checkDegradation: false);

        Published.Should().Equal("wan.speed_completed");
    }

    [Fact]
    public async Task Without_a_background_check_degradation_publishes_with_the_result()
    {
        await Baseline(Result(30, 1000), Result(20, 1000), Result(10, 1000));
        var current = await Store(Result(0, 500));

        await WanSpeedAlertPublisher.PublishAsync(_bus, current, OpenDb, NullLogger.Instance);

        Published.Should().Equal("wan.speed_completed", "wan.speed_degradation");
    }
}
