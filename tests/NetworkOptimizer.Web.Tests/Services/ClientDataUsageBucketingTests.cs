using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class ClientDataUsageBucketingTests
{
    private static UniFiTrafficRateBucket At(DateTime time, double rxBps, double txBps, int interval = 300) => new()
    {
        TimestampMs = new DateTimeOffset(time).ToUnixTimeMilliseconds(),
        IntervalSeconds = interval,
        RxBytesPerSecond = rxBps,
        TxBytesPerSecond = txBps,
    };

    [Fact]
    public void FiveMinuteBucketsPassThroughUnchanged()
    {
        var t = new DateTime(2026, 8, 30, 17, 30, 0, DateTimeKind.Utc);
        var rows = ClientDashboardService.BucketTrafficRate(
            new[] { At(t, 1000, 10), At(t.AddMinutes(5), 2000, 20) }, TimeSpan.FromMinutes(5));

        Assert.Equal(2, rows.Count);
        Assert.Equal(t, rows[0].Time);
        Assert.Equal(300_000, rows[0].DownloadBytes);
        Assert.Equal(3_000, rows[0].UploadBytes);
        Assert.Equal(600_000, rows[1].DownloadBytes);
    }

    [Fact]
    public void FiveMinuteBucketsSumIntoTheHourTheyStartIn()
    {
        var hour = new DateTime(2026, 8, 30, 17, 0, 0, DateTimeKind.Utc);
        var rows = ClientDashboardService.BucketTrafficRate(
            new[] { At(hour.AddMinutes(55), 100, 1), At(hour.AddMinutes(60), 100, 1), At(hour.AddMinutes(65), 100, 1) },
            TimeSpan.FromHours(1));

        Assert.Equal(2, rows.Count);
        Assert.Equal(hour, rows[0].Time);
        Assert.Equal(30_000, rows[0].DownloadBytes);
        Assert.Equal(hour.AddHours(1), rows[1].Time);
        Assert.Equal(60_000, rows[1].DownloadBytes);
        Assert.Equal(600, rows[1].UploadBytes);
    }

    [Fact]
    public void OutOfOrderInputComesBackInTimeOrder()
    {
        var t = new DateTime(2026, 8, 30, 17, 0, 0, DateTimeKind.Utc);
        var rows = ClientDashboardService.BucketTrafficRate(
            new[] { At(t.AddMinutes(10), 1, 1), At(t, 1, 1), At(t.AddMinutes(5), 1, 1) }, TimeSpan.FromMinutes(5));

        Assert.Equal(new[] { t, t.AddMinutes(5), t.AddMinutes(10) }, rows.Select(r => r.Time));
    }

    [Fact]
    public void EmptyInputIsAnEmptyList()
    {
        Assert.Empty(ClientDashboardService.BucketTrafficRate(Array.Empty<UniFiTrafficRateBucket>(), TimeSpan.FromHours(1)));
    }
}
