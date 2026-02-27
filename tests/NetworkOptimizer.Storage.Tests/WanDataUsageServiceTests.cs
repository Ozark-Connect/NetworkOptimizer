using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class WanDataUsageServiceTests
{
    // ========== Billing Cycle Date Calculation ==========

    [Fact]
    public void GetBillingCycleDates_DayAfterBillingDay_CycleStartsThisMonth()
    {
        var refDate = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);
        var (start, end) = WanDataUsageService.GetBillingCycleDates(1, refDate);

        start.Should().Be(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetBillingCycleDates_DayBeforeBillingDay_CycleStartsLastMonth()
    {
        var refDate = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var (start, end) = WanDataUsageService.GetBillingCycleDates(15, refDate);

        start.Should().Be(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetBillingCycleDates_OnBillingDay_CycleStartsToday()
    {
        var refDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var (start, end) = WanDataUsageService.GetBillingCycleDates(15, refDate);

        start.Should().Be(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 4, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetBillingCycleDates_Day1_FirstOfMonth()
    {
        var refDate = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        var (start, end) = WanDataUsageService.GetBillingCycleDates(1, refDate);

        start.Should().Be(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetBillingCycleDates_Day28_EndOfMonth()
    {
        var refDate = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var (start, end) = WanDataUsageService.GetBillingCycleDates(28, refDate);

        // Feb 10 is before 28th, so cycle started Jan 28
        start.Should().Be(new DateTime(2026, 1, 28, 0, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 2, 27, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetBillingCycleDates_ClampsDayAbove28()
    {
        var refDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        // Day 31 should be clamped to 28
        var (start, _) = WanDataUsageService.GetBillingCycleDates(31, refDate);

        start.Day.Should().Be(28);
    }

    [Fact]
    public void GetBillingCycleDates_YearBoundary()
    {
        // January 5 with billing day 15 -> cycle started Dec 15 of previous year
        var refDate = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        var (start, end) = WanDataUsageService.GetBillingCycleDates(15, refDate);

        start.Should().Be(new DateTime(2025, 12, 15, 0, 0, 0, DateTimeKind.Utc));
        end.Should().Be(new DateTime(2026, 1, 14, 0, 0, 0, DateTimeKind.Utc));
    }

    // ========== Usage Calculation from Snapshots ==========

    [Fact]
    public void CalculateUsageFromSnapshots_EmptyList_ReturnsZero()
    {
        var result = WanDataUsageService.CalculateUsageFromSnapshots([]);
        result.Should().Be(0);
    }

    [Fact]
    public void CalculateUsageFromSnapshots_SingleSnapshot_ReturnsZero()
    {
        var snapshots = new List<WanDataUsageSnapshot>
        {
            new() { WanKey = "wan1", RxBytes = 1000, TxBytes = 500, Timestamp = DateTime.UtcNow }
        };

        var result = WanDataUsageService.CalculateUsageFromSnapshots(snapshots);
        result.Should().Be(0);
    }

    [Fact]
    public void CalculateUsageFromSnapshots_TwoSnapshots_ReturnsDelta()
    {
        var snapshots = new List<WanDataUsageSnapshot>
        {
            new() { WanKey = "wan1", RxBytes = 1000, TxBytes = 500, Timestamp = DateTime.UtcNow.AddMinutes(-2) },
            new() { WanKey = "wan1", RxBytes = 2000, TxBytes = 800, Timestamp = DateTime.UtcNow }
        };

        var result = WanDataUsageService.CalculateUsageFromSnapshots(snapshots);
        // RxDelta = 1000, TxDelta = 300 => 1300
        result.Should().Be(1300);
    }

    [Fact]
    public void CalculateUsageFromSnapshots_MultipleSnapshots_SumsDeltas()
    {
        var now = DateTime.UtcNow;
        var snapshots = new List<WanDataUsageSnapshot>
        {
            new() { WanKey = "wan1", RxBytes = 1000, TxBytes = 500, Timestamp = now.AddMinutes(-6) },
            new() { WanKey = "wan1", RxBytes = 2000, TxBytes = 1000, Timestamp = now.AddMinutes(-4) },
            new() { WanKey = "wan1", RxBytes = 3500, TxBytes = 1500, Timestamp = now.AddMinutes(-2) },
            new() { WanKey = "wan1", RxBytes = 4000, TxBytes = 2000, Timestamp = now }
        };

        var result = WanDataUsageService.CalculateUsageFromSnapshots(snapshots);
        // Total Rx delta = 3000, Total Tx delta = 1500 => 4500
        result.Should().Be(4500);
    }

    [Fact]
    public void CalculateUsageFromSnapshots_CounterReset_SkipsResetDelta()
    {
        var now = DateTime.UtcNow;
        var snapshots = new List<WanDataUsageSnapshot>
        {
            new() { WanKey = "wan1", RxBytes = 10000, TxBytes = 5000, Timestamp = now.AddMinutes(-6) },
            new() { WanKey = "wan1", RxBytes = 15000, TxBytes = 7000, Timestamp = now.AddMinutes(-4) },
            // Counter reset (gateway reboot) - values drop
            new() { WanKey = "wan1", RxBytes = 100, TxBytes = 50, IsCounterReset = true, Timestamp = now.AddMinutes(-2) },
            new() { WanKey = "wan1", RxBytes = 2000, TxBytes = 1000, Timestamp = now }
        };

        var result = WanDataUsageService.CalculateUsageFromSnapshots(snapshots);
        // Snapshot 1->2: Rx=5000, Tx=2000 = 7000
        // Snapshot 2->3: SKIPPED (counter reset)
        // Snapshot 3->4: Rx=1900, Tx=950 = 2850
        // Total = 9850
        result.Should().Be(9850);
    }

    [Fact]
    public void CalculateUsageFromSnapshots_CounterResetAtStart_SkipsFirstDelta()
    {
        var now = DateTime.UtcNow;
        var snapshots = new List<WanDataUsageSnapshot>
        {
            new() { WanKey = "wan1", RxBytes = 50000, TxBytes = 20000, Timestamp = now.AddMinutes(-4) },
            // Reset detected
            new() { WanKey = "wan1", RxBytes = 500, TxBytes = 200, IsCounterReset = true, Timestamp = now.AddMinutes(-2) },
            new() { WanKey = "wan1", RxBytes = 1500, TxBytes = 700, Timestamp = now }
        };

        var result = WanDataUsageService.CalculateUsageFromSnapshots(snapshots);
        // Snapshot 0->1: SKIPPED (reset)
        // Snapshot 1->2: Rx=1000, Tx=500 = 1500
        result.Should().Be(1500);
    }

    // ========== Large Values (multi-GB) ==========

    [Fact]
    public void CalculateUsageFromSnapshots_LargeValues_HandlesCorrectly()
    {
        var now = DateTime.UtcNow;
        var oneGb = 1024L * 1024 * 1024;
        var snapshots = new List<WanDataUsageSnapshot>
        {
            new() { WanKey = "wan1", RxBytes = oneGb, TxBytes = oneGb / 2, Timestamp = now.AddMinutes(-2) },
            new() { WanKey = "wan1", RxBytes = oneGb * 3, TxBytes = oneGb, Timestamp = now }
        };

        var result = WanDataUsageService.CalculateUsageFromSnapshots(snapshots);
        // Rx delta = 2GB, Tx delta = 0.5GB
        result.Should().Be(oneGb * 2 + oneGb / 2);
    }
}
