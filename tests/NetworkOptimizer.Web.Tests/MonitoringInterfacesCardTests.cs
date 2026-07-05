using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// This project has no Blazor component-test harness (no bunit), so the Remove flow's
/// retry/force-delete decision is covered here via the extracted pure function instead of
/// through the component itself. See MonitoringInterfacesCard.razor's RemoveInterface for
/// how the UI wires this decision to the DB delete and the per-row armed-state flag - the
/// parts of that wiring that touch StateHasChanged/_busyId still need manual verification.
/// _forceRemoveArmed itself and its DisarmAllForceRemove clear are exposed internal (see
/// NetworkOptimizer.Web.csproj InternalsVisibleTo) and tested directly below, since
/// constructing the component needs no renderer or injected services for that one method.
/// </summary>
public class MonitoringInterfacesCardTests
{
    [Fact]
    public void DecideRemoveOutcome_CleanupSucceeded_DeletesRowAndDoesNotArm()
    {
        var outcome = MonitoringInterfacesCard.DecideRemoveOutcome(wasArmed: false, cleanupSucceeded: true, name: "modem0");

        outcome.DeleteRow.Should().BeTrue();
        outcome.Arm.Should().BeFalse();
        outcome.Success.Should().BeTrue();
        outcome.Message.Should().Contain("Removed").And.Contain("modem0");
    }

    [Fact]
    public void DecideRemoveOutcome_CleanupSucceeded_AfterPriorArm_StillDeletesAndDisarms()
    {
        // A retry (second click) can succeed too - not just fail-again. Success always wins
        // regardless of the prior armed state, and must not leave anything armed behind.
        var outcome = MonitoringInterfacesCard.DecideRemoveOutcome(wasArmed: true, cleanupSucceeded: true, name: "modem0");

        outcome.DeleteRow.Should().BeTrue();
        outcome.Arm.Should().BeFalse();
        outcome.Success.Should().BeTrue();
    }

    [Fact]
    public void DecideRemoveOutcome_FirstFailure_KeepsRowAndArms()
    {
        // First failure: the row must survive so there's still a UI handle to retry from -
        // a stranded gateway (macvlan/alias rules/cron watchdog still live) with no row left
        // to act on is worse than a row that says "not fully removed".
        var outcome = MonitoringInterfacesCard.DecideRemoveOutcome(wasArmed: false, cleanupSucceeded: false, name: "starlink0");

        outcome.DeleteRow.Should().BeFalse();
        outcome.Arm.Should().BeTrue();
        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("Click Remove again");
    }

    [Fact]
    public void DecideRemoveOutcome_SecondFailure_ForceDeletesAndWarnsAboutResidualState()
    {
        // Second click on an already-armed row: delete it anyway, but the message must be
        // explicit that gateway state might still be there and point at manual cleanup -
        // silently deleting the row would strand that state with no UI handle left at all.
        var outcome = MonitoringInterfacesCard.DecideRemoveOutcome(wasArmed: true, cleanupSucceeded: false, name: "starlink0");

        outcome.DeleteRow.Should().BeTrue();
        outcome.Arm.Should().BeFalse();
        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("may remain").And.Contain("manual cleanup");
    }

    [Fact]
    public void DisarmAllForceRemove_ClearsEntireArmedSet_NotJustOneRow()
    {
        // The armed flag is a transient "you just saw this error" confirmation - any edit,
        // deploy, or status-check interaction anywhere in the UI invalidates it, even on rows
        // other than the one armed. Scoping the clear to only the current row (the original
        // bug) let an armed row stay armed indefinitely while the user worked elsewhere, so a
        // much-later click on it could force-delete it as if it had just failed again.
        var card = new MonitoringInterfacesCard();
        card._forceRemoveArmed.Add(3);
        card._forceRemoveArmed.Add(7);
        card._forceRemoveArmed.Add(12);

        card.DisarmAllForceRemove();

        card._forceRemoveArmed.Should().BeEmpty();
    }
}
