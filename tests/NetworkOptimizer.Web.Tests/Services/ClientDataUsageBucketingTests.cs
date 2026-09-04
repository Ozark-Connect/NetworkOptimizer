using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class ClientDataUsageBucketingTests
{
    // The console stamps a bucket with its end, so a bucket that ENDS at `end` covers the five
    // minutes before it.
    private static UniFiTrafficRateBucket EndingAt(DateTime end, double rxBps, double txBps, int interval = 300) => new()
    {
        TimestampMs = new DateTimeOffset(end).ToUnixTimeMilliseconds(),
        IntervalSeconds = interval,
        RxBytesPerSecond = rxBps,
        TxBytesPerSecond = txBps,
    };

    [Fact]
    public void FiveMinuteBucketsAreFiledByTheirStart()
    {
        var t = new DateTime(2026, 8, 30, 17, 30, 0, DateTimeKind.Utc);
        var rows = ClientDashboardService.BucketTrafficRate(
            new[] { EndingAt(t, 1000, 10), EndingAt(t.AddMinutes(5), 2000, 20) }, TimeSpan.FromMinutes(5));

        Assert.Equal(2, rows.Count);
        Assert.Equal(t.AddMinutes(-5), rows[0].Time);
        Assert.Equal(300_000, rows[0].DownloadBytes);
        Assert.Equal(3_000, rows[0].UploadBytes);
        Assert.Equal(t, rows[1].Time);
        Assert.Equal(600_000, rows[1].DownloadBytes);
    }

    [Fact]
    public void TheBucketEndingOnTheHourBelongsToTheHourBefore()
    {
        var hour = new DateTime(2026, 8, 30, 17, 0, 0, DateTimeKind.Utc);
        // Ends at 17:55, 18:00, 18:05: the first two are the 17:00 hour, only the last is 18:00.
        var rows = ClientDashboardService.BucketTrafficRate(
            new[] { EndingAt(hour.AddMinutes(55), 100, 1), EndingAt(hour.AddMinutes(60), 100, 1), EndingAt(hour.AddMinutes(65), 100, 1) },
            TimeSpan.FromHours(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(hour, rows[0].Time);
        Assert.Equal(60_000, rows[0].DownloadBytes);
        Assert.Equal(hour.AddHours(1), rows[1].Time);
        Assert.Equal(30_000, rows[1].DownloadBytes);
        Assert.Equal(300, rows[1].UploadBytes);
    }

    [Fact]
    public void OutOfOrderInputComesBackInTimeOrder()
    {
        var t = new DateTime(2026, 8, 30, 17, 0, 0, DateTimeKind.Utc);
        var rows = ClientDashboardService.BucketTrafficRate(
            new[] { EndingAt(t.AddMinutes(15), 1, 1), EndingAt(t.AddMinutes(5), 1, 1), EndingAt(t.AddMinutes(10), 1, 1) }, TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { t, t.AddMinutes(5), t.AddMinutes(10) }, rows.Select(r => r.Time));
    }

    [Fact]
    public void EmptyInputIsAnEmptyList()
    {
        Assert.Empty(ClientDashboardService.BucketTrafficRate(Array.Empty<UniFiTrafficRateBucket>(), TimeSpan.FromHours(1)));
    }
}
