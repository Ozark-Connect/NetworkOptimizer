using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The lazy Console-roster nudge: a membership change suggests a re-read after the Console has had
/// time to digest it, coalesced and floored so roam churn cannot become a continuous poll.
/// </summary>
public class ConsoleRosterNudgeTests
{
    private static readonly DateTime T0 = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoChange_NeverSuggestsARefresh()
    {
        var nudge = new ConsoleRosterNudge();
        nudge.ShouldRefresh(T0, T0 + TimeSpan.FromMinutes(5)).Should().BeFalse();
    }

    [Fact]
    public void Change_SuggestsARefresh_OnlyAfterTheSettleDelay()
    {
        var nudge = new ConsoleRosterNudge();
        var snapshotAt = T0 - TimeSpan.FromSeconds(15);
        nudge.NoteMembershipChange(T0);

        nudge.ShouldRefresh(snapshotAt, T0 + TimeSpan.FromSeconds(5)).Should().BeFalse("the Console has not settled yet");
        nudge.ShouldRefresh(snapshotAt, T0 + ConsoleRosterNudge.ConsoleSettleDelay).Should().BeTrue();
    }

    [Fact]
    public void SnapshotTakenAfterTheDueInstant_IsAlreadyFresh()
    {
        var nudge = new ConsoleRosterNudge();
        nudge.NoteMembershipChange(T0);

        var afterDue = T0 + ConsoleRosterNudge.ConsoleSettleDelay + TimeSpan.FromSeconds(1);
        nudge.ShouldRefresh(afterDue, afterDue + TimeSpan.FromSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void EveryStaleConsumer_SeesTheSuggestion_NotJustTheFirst()
    {
        var nudge = new ConsoleRosterNudge();
        var snapshotAt = T0 - TimeSpan.FromSeconds(15);
        nudge.NoteMembershipChange(T0);

        var due = T0 + ConsoleRosterNudge.ConsoleSettleDelay;
        nudge.ShouldRefresh(snapshotAt, due).Should().BeTrue();
        nudge.ShouldRefresh(snapshotAt, due + TimeSpan.FromSeconds(2)).Should().BeTrue();
    }

    [Fact]
    public void ChangesWhilePending_Coalesce()
    {
        var nudge = new ConsoleRosterNudge();
        var snapshotAt = T0 - TimeSpan.FromSeconds(15);
        nudge.NoteMembershipChange(T0);
        nudge.NoteMembershipChange(T0 + TimeSpan.FromSeconds(5));

        // The second change does not push the first suggestion out.
        nudge.ShouldRefresh(snapshotAt, T0 + ConsoleRosterNudge.ConsoleSettleDelay).Should().BeTrue();
    }

    [Fact]
    public void ChurnIsFloored_BetweenSuggestions()
    {
        var nudge = new ConsoleRosterNudge();
        nudge.NoteMembershipChange(T0);
        var firstDue = T0 + ConsoleRosterNudge.ConsoleSettleDelay;
        nudge.ShouldRefresh(T0 - TimeSpan.FromSeconds(15), firstDue).Should().BeTrue();

        // A change right after the first fire cannot come due before the floor.
        nudge.NoteMembershipChange(firstDue + TimeSpan.FromSeconds(1));
        var snapshotAt = firstDue + TimeSpan.FromSeconds(2);
        nudge.ShouldRefresh(snapshotAt, firstDue + TimeSpan.FromSeconds(12)).Should().BeFalse("the floor holds");
        nudge.ShouldRefresh(snapshotAt, firstDue + ConsoleRosterNudge.MinInterval).Should().BeTrue();
    }
}
