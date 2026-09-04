using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentNoiseFloorHistoryTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_median_until_an_hours_worth_of_readings()
    {
        var history = new ApAgentNoiseFloorHistory();
        for (var i = 0; i < ApAgentNoiseFloorHistory.MinSamples - 1; i++)
            history.Record("aa:bb:cc:dd:ee:01", "wifi2", -92, T0.AddSeconds(i * 30));

        history.HourMedian("aa:bb:cc:dd:ee:01", "wifi2", T0.AddMinutes(50)).Should().BeNull();

        history.Record("aa:bb:cc:dd:ee:01", "wifi2", -92, T0.AddMinutes(50));
        history.HourMedian("aa:bb:cc:dd:ee:01", "wifi2", T0.AddMinutes(50)).Should().Be(-92);
    }

    [Fact]
    public void The_median_ignores_a_burst_that_the_latest_sample_would_show()
    {
        var history = new ApAgentNoiseFloorHistory();
        for (var i = 0; i < 110; i++)
            history.Record("aa:bb:cc:dd:ee:01", "wifi2", -92, T0.AddSeconds(i * 30));
        for (var i = 110; i < 120; i++)
            history.Record("aa:bb:cc:dd:ee:01", "wifi2", -60, T0.AddSeconds(i * 30));

        history.HourMedian("aa:bb:cc:dd:ee:01", "wifi2", T0.AddHours(1)).Should().Be(-92);
    }

    [Fact]
    public void Readings_older_than_the_window_fall_out_and_sentinels_are_dropped()
    {
        var history = new ApAgentNoiseFloorHistory();
        for (var i = 0; i < 120; i++)
            history.Record("aa:bb:cc:dd:ee:01", "wifi2", -92, T0.AddSeconds(i * 30));
        history.Record("aa:bb:cc:dd:ee:01", "wifi2", 0, T0.AddHours(1));
        history.Record("aa:bb:cc:dd:ee:01", "wifi2", null, T0.AddHours(1));

        // Two hours on, nothing in the window remains.
        history.HourMedian("aa:bb:cc:dd:ee:01", "wifi2", T0.AddHours(3)).Should().BeNull();
    }
}
