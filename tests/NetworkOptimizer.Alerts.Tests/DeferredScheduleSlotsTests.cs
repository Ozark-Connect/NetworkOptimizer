using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

/// <summary>
/// Retries of a slot must not move a task's cadence: the run after them counts from the slot
/// they deferred from.
/// </summary>
public class DeferredScheduleSlotsTests
{
    private static readonly DateTime Slot = new(2026, 11, 3, 4, 14, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_NoDeferral_ReturnsTheScheduledSlot()
    {
        var slots = new DeferredScheduleSlots();

        slots.Resolve("site", 1, Slot, deferring: false).Should().Be(Slot);
    }

    [Fact]
    public void Resolve_AfterRetries_ReturnsTheFirstDeferredSlotOnce()
    {
        var slots = new DeferredScheduleSlots();

        slots.Resolve("site", 1, Slot, deferring: true);
        slots.Resolve("site", 1, Slot.AddMinutes(10), deferring: true);

        slots.Resolve("site", 1, Slot.AddMinutes(20), deferring: false).Should().Be(Slot);
        slots.Resolve("site", 1, Slot.AddHours(12), deferring: false).Should().Be(Slot.AddHours(12));
    }

    [Fact]
    public void Resolve_KeysOnSiteAndTask()
    {
        var slots = new DeferredScheduleSlots();

        slots.Resolve("site-a", 1, Slot, deferring: true);

        slots.Resolve("site-b", 1, Slot.AddMinutes(10), deferring: false).Should().Be(Slot.AddMinutes(10));
        slots.Resolve("site-a", 2, Slot.AddMinutes(10), deferring: false).Should().Be(Slot.AddMinutes(10));
        slots.Resolve("site-a", 1, Slot.AddMinutes(10), deferring: false).Should().Be(Slot);
    }

    [Fact]
    public void NextRunAfterRetries_KeepsTheTwelveHourCadence()
    {
        // A 12h audit at 04:14 that retried until 04:34 still runs next at 16:14
        var slots = new DeferredScheduleSlots();
        slots.Resolve("site", 1, Slot, deferring: true);
        slots.Resolve("site", 1, Slot.AddMinutes(10), deferring: true);

        var slotBase = slots.Resolve("site", 1, Slot.AddMinutes(20), deferring: false);
        var next = ScheduleService.CalculateNextRun(720, scheduledRunTime: slotBase);

        // CalculateNextRun walks forward past now, so assert the grid rather than one date
        ((next - Slot).TotalMinutes % 720).Should().Be(0);
    }
}
