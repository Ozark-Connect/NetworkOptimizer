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
            new[] { EndingAt(t, 1000, 10), EndingAt(t.AddMinutes(5), 2000, 20) }, TimeSpan.FromMinutes(5), TimeZoneInfo.Utc);

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
            TimeSpan.FromHours(1), TimeZoneInfo.Utc);

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
            new[] { EndingAt(t.AddMinutes(15), 1, 1), EndingAt(t.AddMinutes(5), 1, 1), EndingAt(t.AddMinutes(10), 1, 1) }, TimeSpan.FromMinutes(5), TimeZoneInfo.Utc);

        Assert.Equal(new[] { t, t.AddMinutes(5), t.AddMinutes(10) }, rows.Select(r => r.Time));
    }

    [Fact]
    public void DayBucketsStartAtMidnightInTheGivenZone()
    {
        // 03:30 UTC is still the previous day in Chicago, so the edge is 05:00 UTC of Aug 29.
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var t = new DateTime(2026, 8, 30, 3, 30, 0, DateTimeKind.Utc);
        var start = ClientDashboardService.BucketStart(t, TimeSpan.FromDays(1), chicago);

        Assert.Equal(DateTimeKind.Utc, start.Kind);
        Assert.Equal(new DateTime(2026, 8, 29, 5, 0, 0, DateTimeKind.Utc), start);
        // Every instant of that local day files under the same edge.
        Assert.Equal(start, ClientDashboardService.BucketStart(start, TimeSpan.FromDays(1), chicago));
        Assert.Equal(start, ClientDashboardService.BucketStart(start.AddHours(23), TimeSpan.FromDays(1), chicago));
    }

    [Fact]
    public void SubDayBucketsTruncateOnTheUtcClock()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var t = new DateTime(2026, 8, 30, 17, 37, 0, DateTimeKind.Utc);
        Assert.Equal(t.AddMinutes(-2), ClientDashboardService.BucketStart(t, TimeSpan.FromMinutes(5), chicago));
        Assert.Equal(t.AddMinutes(-37), ClientDashboardService.BucketStart(t, TimeSpan.FromHours(1), chicago));
    }

    [Fact]
    public void EmptyInputIsAnEmptyList()
    {
        Assert.Empty(ClientDashboardService.BucketTrafficRate(Array.Empty<UniFiTrafficRateBucket>(), TimeSpan.FromHours(1), TimeZoneInfo.Utc));
    }
}
