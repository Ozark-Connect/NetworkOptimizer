using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentChannelMoveTrackerTests
{
    private const string Ap = "aa:bb:cc:dd:ee:01";
    private static readonly DateTime MovedAt = new(2026, 9, 2, 17, 20, 0, DateTimeKind.Utc);

    private static ApAgentChannelMove Move(int from, int to, int width = 320) => new()
    {
        ApMac = Ap, Band = "6e", FromChannel = from, FromWidth = width, ToChannel = to, ToWidth = width, At = MovedAt
    };

    private static ApAgentAirtimeHour Hour(DateTime at, int channel, double interference) =>
        new(Ap, "6e", at, channel, 320, 30, interference, 120, at.AddMinutes(59));

    [Fact]
    public void Landing_compares_the_guess_with_the_measured_block_once()
    {
        var tracker = new ApAgentChannelMoveTracker();
        tracker.Record(Move(69, 101));

        // Guess for 101 at 320 MHz is the lower block 65-125; the radio measured center 127, block 97-157.
        var landing = tracker.CheckLanding(Ap, "6e", 101, 320, 127);

        landing.Should().NotBeNull();
        landing!.Value.Predicted.Should().Be((65, 125));
        landing.Value.Landed.Should().Be((97, 157));
        tracker.CheckLanding(Ap, "6e", 101, 320, 127).Should().BeNull("the check runs once");
    }

    [Fact]
    public void Landing_waits_for_the_destination_channel()
    {
        var tracker = new ApAgentChannelMoveTracker();
        tracker.Record(Move(69, 101));

        tracker.CheckLanding(Ap, "6e", 69, 320, 63).Should().BeNull("still reporting the origin");
        tracker.CheckLanding(Ap, "6e", 101, 320, 95).Should().NotBeNull();
    }

    [Fact]
    public void The_verdict_compares_the_first_full_hour_after_with_the_last_hour_before()
    {
        var tracker = new ApAgentChannelMoveTracker();
        var move = Move(69, 101);
        tracker.Record(move);
        var hours = new[]
        {
            Hour(MovedAt.AddHours(-2), 69, 28),
            Hour(MovedAt.AddHours(-1), 69, 31),
            Hour(MovedAt.AddHours(1), 101, 9),
        };

        tracker.TryEvaluate(move, hours, MovedAt.AddMinutes(30)).Should().BeFalse("not due yet");
        tracker.TryEvaluate(move, hours, MovedAt.AddHours(2)).Should().BeTrue();

        move.Outcome.Should().Be(MoveOutcome.Improved);
        move.InterferenceBefore.Should().Be(31);
        move.InterferenceAfter.Should().Be(9);
        tracker.TryEvaluate(move, hours, MovedAt.AddHours(3)).Should().BeFalse("a verdict is reached once");
    }

    [Theory]
    [InlineData(30, 28, MoveOutcome.Same)]
    [InlineData(12, 30, MoveOutcome.Worse)]
    [InlineData(30, 25, MoveOutcome.Improved)]
    public void The_dead_band_is_five_points_either_way(double before, double after, MoveOutcome expected)
    {
        var tracker = new ApAgentChannelMoveTracker();
        var move = Move(69, 101);
        tracker.Record(move);
        var hours = new[] { Hour(MovedAt.AddHours(-1), 69, before), Hour(MovedAt.AddHours(1), 101, after) };

        tracker.TryEvaluate(move, hours, MovedAt.AddHours(2)).Should().BeTrue();

        move.Outcome.Should().Be(expected);
    }

    [Fact]
    public void A_move_with_no_destination_hour_is_given_up_on_after_three_hours()
    {
        var tracker = new ApAgentChannelMoveTracker();
        var move = Move(69, 101);
        tracker.Record(move);
        var hours = new[] { Hour(MovedAt.AddHours(-1), 69, 30) };

        tracker.TryEvaluate(move, hours, MovedAt.AddHours(2)).Should().BeFalse();
        tracker.For(Ap, "6e").Should().NotBeNull("still waiting");

        tracker.TryEvaluate(move, hours, MovedAt.AddHours(4)).Should().BeFalse();
        tracker.For(Ap, "6e").Should().BeNull("given up");
    }
}
