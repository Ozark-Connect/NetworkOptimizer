using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

public class ScheduleCalculationTests
{
    [Fact]
    public void NonAnchored_WithScheduledRunTime_BasesNextRunOnScheduledTime()
    {
        // A task scheduled at :00 with 60-min frequency should next run at :00,
        // not at :02 (when execution finishes)
        var scheduledTime = DateTime.UtcNow.AddMinutes(-2); // ran 2 min ago

        var next = ScheduleService.CalculateNextRun(60, scheduledRunTime: scheduledTime);

        // next should be scheduledTime + 60 min (not UtcNow + 60 min)
        var expected = scheduledTime.AddMinutes(60);
        next.Should().BeCloseTo(expected, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void NonAnchored_WithScheduledRunTime_WalksForwardIfInPast()
    {
        // If the scheduled time + frequency is still in the past, walk forward
        var scheduledTime = DateTime.UtcNow.AddMinutes(-130); // 130 min ago, freq=60

        var next = ScheduleService.CalculateNextRun(60, scheduledRunTime: scheduledTime);

        // Should walk forward: -130+60=-70 (past), -70+60=-10 (past), -10+60=+50 (future)
        next.Should().BeAfter(DateTime.UtcNow);
        // Should still be aligned to the original schedule grid
        var offset = (next - scheduledTime).TotalMinutes;
        (offset % 60).Should().BeApproximately(0, 0.01);
    }

    [Fact]
    public void NonAnchored_WithoutScheduledRunTime_FallsBackToUtcNow()
    {
        var before = DateTime.UtcNow;

        var next = ScheduleService.CalculateNextRun(60);

        var after = DateTime.UtcNow;
        // Should be approximately UtcNow + 60 min
        next.Should().BeOnOrAfter(before.AddMinutes(60));
        next.Should().BeOnOrBefore(after.AddMinutes(60));
    }

    [Fact]
    public void NonAnchored_FrequencyZero_ReturnsFallback()
    {
        var before = DateTime.UtcNow;

        var next = ScheduleService.CalculateNextRun(0, scheduledRunTime: before.AddMinutes(-10));

        // Falls back to now + 60 min (guards against infinite loop)
        next.Should().BeOnOrAfter(before.AddMinutes(60));
        next.Should().BeOnOrBefore(DateTime.UtcNow.AddMinutes(60).AddSeconds(1));
    }

    [Fact]
    public void Anchored_IgnoresScheduledRunTime()
    {
        // Anchored path should still work normally (uses startHour/startMinute)
        var now = DateTime.UtcNow;
        var futureHour = (now.Hour + 2) % 24;

        var next = ScheduleService.CalculateNextRun(
            frequencyMinutes: 1440,
            startHour: futureHour,
            startMinute: 0,
            scheduledRunTime: now.AddMinutes(-10));

        // Should be anchored to futureHour:00, not based on scheduledRunTime
        next.Hour.Should().Be(futureHour);
        next.Minute.Should().Be(0);
    }

    [Fact]
    public void Anchored_FrequencyZero_ReturnsFallback()
    {
        var before = DateTime.UtcNow;

        var next = ScheduleService.CalculateNextRun(0, startHour: 6, startMinute: 0);

        // Falls back to now + 60 min
        next.Should().BeOnOrAfter(before.AddMinutes(60));
        next.Should().BeOnOrBefore(DateTime.UtcNow.AddMinutes(60).AddSeconds(1));
    }

    [Fact]
    public void NonAnchored_ScheduledTimeJustBeforeNow_NextRunIsInFuture()
    {
        // Edge case: scheduled exactly frequencyMinutes ago (next would be ~now)
        var scheduledTime = DateTime.UtcNow.AddMinutes(-60);

        var next = ScheduleService.CalculateNextRun(60, scheduledRunTime: scheduledTime);

        // scheduledTime + 60 ≈ now, but the while loop ensures next > now
        next.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public void Anchored_HourlyWithLateAnchor_FindsNextHourlySlot()
    {
        // Anchor at a future hour today with 60-min frequency should still find
        // the next hourly slot, not jump all the way to the anchor time.
        var now = DateTime.UtcNow;
        var futureHour = (now.Hour + 3) % 24; // anchor 3 hours from now

        var next = ScheduleService.CalculateNextRun(
            frequencyMinutes: 60,
            startHour: futureHour,
            startMinute: 0);

        // With 60-min frequency, next slot should be within ~61 minutes, not 3 hours
        var maxExpected = now.AddMinutes(62);
        next.Should().BeBefore(maxExpected,
            "hourly task should find the next hourly slot, not jump to the distant anchor time");
        next.Should().BeAfter(now);
    }
}
