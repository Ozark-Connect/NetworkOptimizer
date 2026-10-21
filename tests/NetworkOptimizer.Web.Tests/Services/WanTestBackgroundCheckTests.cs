using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class WanTestBackgroundCheckTests
{
    private static readonly DateTime T0 = new(2026, 10, 14, 23, 0, 0, DateTimeKind.Utc);

    private static MonitoringInfluxClient.WanRatePoint Point(double seconds, double downMbps, double upMbps) => new()
    {
        Time = T0.AddSeconds(seconds),
        DownloadBps = downMbps * 1_000_000,
        UploadBps = upMbps * 1_000_000,
    };

    // Idle 0-10 s, download 12-20 s, upload 22-30 s.
    private static readonly WanTestPhases Phases = new(
        (T0, T0.AddSeconds(10)),
        (T0.AddSeconds(12), T0.AddSeconds(20)),
        (T0.AddSeconds(22), T0.AddSeconds(30)));

    [Fact]
    public void Idle_phase_counters_are_background()
    {
        var points = new[] { Point(5, 200, 10), Point(10, 210, 12) };

        var (down, up) = WanTestBackgroundCheck.Measure(points, Phases);

        down.Should().BeApproximately(205, 0.01);
        up.Should().BeApproximately(11, 0.01);
    }

    [Fact]
    public void A_point_straddling_the_end_of_the_idle_phase_is_not_idle_background()
    {
        // The 12.5 s point covers 7.5-12.5 s: half idle, half the download ramping to 1 Gbps.
        var points = new[] { Point(5, 2, 1), Point(10, 2, 1), Point(12.5, 500, 10) };

        var (down, _) = WanTestBackgroundCheck.Measure(points, Phases);

        down.Should().BeApproximately(2, 0.01);
    }

    [Fact]
    public void Loaded_phase_counters_never_count_as_background()
    {
        // A burst crest above the test's own rate during the download (1227 Mbps on a clean test).
        var points = new[] { Point(5, 0, 0), Point(10, 0, 0), Point(15, 1227, 20), Point(20, 537, 350) };

        var (down, up) = WanTestBackgroundCheck.Measure(points, Phases);

        down.Should().Be(0);
        up.Should().Be(0);
    }

    [Fact]
    public void No_counter_point_inside_the_idle_phase_is_unknown_not_zero()
    {
        var (down, up) = WanTestBackgroundCheck.Measure(new[] { Point(-30, 500, 500), Point(15, 900, 10) }, Phases);

        down.Should().BeNull();
        up.Should().BeNull();
    }

    [Theory]
    [InlineData(800, 1000, 200, true)]   // below 85%, background covers the whole 200 Mbps gap
    [InlineData(800, 1000, 100, true)]   // exactly half the gap
    [InlineData(800, 1000, 20, false)]   // a real shortfall: background explains little of it
    [InlineData(900, 1000, 300, false)]  // above 85% of plan: never invalid
    public void Invalid_when_below_expected_and_background_explains_the_gap(
        double testMbps, double planMbps, double backgroundMbps, bool invalid)
    {
        WanTestBackgroundCheck.IsInvalid(testMbps, planMbps, backgroundMbps).Should().Be(invalid);
    }

    [Fact]
    public void Invalid_message_names_only_the_directions_the_background_explains()
    {
        var result = new NetworkOptimizer.Storage.Models.Iperf3Result
        {
            DownloadBitsPerSecond = 800_000_000, UploadBitsPerSecond = 930_000_000,
            ExpectedDownloadMbps = 1000, ExpectedUploadMbps = 1000,
            BackgroundDownloadMbps = 210, BackgroundUploadMbps = 5,
            BackgroundLoadInvalid = true,
        };

        WanTestBackgroundCheck.InvalidMessage(result).Should().Be(
            "Not a valid test: 210 Mbps of other download traffic was on this WAN during the test, "
            + "enough to explain the shortfall from your 1000 Mbps plan. ISP Health ignores this result.");

        result.BackgroundLoadInvalid = false;
        WanTestBackgroundCheck.InvalidMessage(result).Should().BeNull();
    }

    [Fact]
    public void No_plan_or_no_background_is_never_invalid()
    {
        WanTestBackgroundCheck.IsInvalid(500, null, 400).Should().BeFalse();
        WanTestBackgroundCheck.IsInvalid(500, 1000, null).Should().BeFalse();
    }
}
