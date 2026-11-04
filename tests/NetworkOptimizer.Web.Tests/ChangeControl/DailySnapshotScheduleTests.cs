using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class DailySnapshotScheduleTests
{
    private static ChangeControlSettings At(int hour) => new() { DailySnapshotHour = hour, DailySnapshotMinute = 0 };

    [Fact]
    public void The_daily_snapshot_runs_on_the_site_clock()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        // 04:00 on Jan 16 in Tokyo: today's 03:00 slot was 18:00 UTC on Jan 15.
        var now = new DateTime(2026, 1, 15, 19, 0, 0, DateTimeKind.Utc);

        ChangeControlSiteEngine.DailyDue(At(3), new ChangeDetectionState { LastDailySnapshotAt = now.AddHours(-2) }, now, tokyo)
            .Should().BeTrue();
        ChangeControlSiteEngine.DailyDue(At(3), new ChangeDetectionState { LastDailySnapshotAt = now.AddMinutes(-30) }, now, tokyo)
            .Should().BeFalse();
    }

    [Fact]
    public void A_slot_in_the_spring_forward_gap_runs_after_it()
    {
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        // 02:00 does not exist in Chicago on 2026-03-08; the slot moves to 03:00 CDT, 08:00 UTC.
        var now = new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc);

        ChangeControlSiteEngine.DailyDue(At(2), new ChangeDetectionState { LastDailySnapshotAt = now.AddMinutes(-90) }, now, chicago)
            .Should().BeTrue();
        ChangeControlSiteEngine.DailyDue(At(2), new ChangeDetectionState { LastDailySnapshotAt = now.AddMinutes(-30) }, now, chicago)
            .Should().BeFalse();
    }
}
