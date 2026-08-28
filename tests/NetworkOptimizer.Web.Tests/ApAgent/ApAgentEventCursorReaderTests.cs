using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Resuming an access point's event ring. The ring is bounded and the agent keeps no state across a
/// restart, so the two things that must never happen are silently replaying a window that has been
/// overwritten and silently believing nothing happened when the numbering began again.
/// </summary>
public class ApAgentEventCursorReaderTests
{
    private static readonly DateTime Started = new(2026, 8, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T0 = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentEventsPayload Payload(
        DateTime startedAt, bool truncated, long oldest, long newest, long dropped, params ulong[] seqs)
        => new()
        {
            AgentStartedAt = startedAt,
            Truncated = truncated,
            CollectedAt = T0,
            Ring = new ApAgentRingStats { Capacity = 1024, OldestSeq = oldest, NewestSeq = newest, Dropped = dropped },
            Events = seqs.Select(s => new ApAgentEvent
            {
                Seq = s,
                Type = ApAgentEventTypes.Assoc,
                Mac = "00:11:22:33:44:55",
                Vap = "ath0",
                CollectedAt = T0.AddSeconds(s),
            }).ToList(),
        };

    [Fact]
    public void A_cold_start_asks_for_the_whole_ring()
        => ApAgentEventCursorReader.SinceFor(null).Should().Be(0);

    [Fact]
    public void A_stored_cursor_asks_for_what_comes_after_it()
        => ApAgentEventCursorReader.SinceFor(new ApAgentEventCursor { LastSeq = 42 }).Should().Be(42);

    [Fact]
    public void Only_events_after_the_cursor_are_taken_and_the_cursor_advances_to_the_last_one()
    {
        var window = ApAgentEventCursorReader.Read(10, Started, Payload(Started, false, 5, 13, 0, 9, 10, 11, 12, 13));

        window.Events.Select(e => e.Seq).Should().Equal(11ul, 12ul, 13ul);
        window.NextSeq.Should().Be(13);
        window.Gap.Should().BeFalse();
        window.RefetchFromStart.Should().BeFalse();
    }

    [Fact]
    public void An_empty_reply_leaves_the_cursor_where_it_was()
    {
        var window = ApAgentEventCursorReader.Read(13, Started, Payload(Started, false, 5, 13, 0));

        window.Events.Should().BeEmpty();
        window.NextSeq.Should().Be(13, "nothing was consumed, so nothing may be skipped");
    }

    [Fact]
    public void A_ring_that_overwrote_the_requested_window_reports_a_gap()
    {
        var window = ApAgentEventCursorReader.Read(10, Started, Payload(Started, true, 400, 402, 390, 401, 402));

        window.Gap.Should().BeTrue("the events between 10 and 400 are gone and must not be interpolated over");
        window.Events.Should().HaveCount(2);
        window.NextSeq.Should().Be(402);
        window.DroppedEvents.Should().Be(390);
    }

    [Fact]
    public void Truncation_on_a_first_read_is_not_a_gap_in_our_data()
    {
        var window = ApAgentEventCursorReader.Read(0, null, Payload(Started, true, 400, 401, 390, 400, 401));

        window.Gap.Should().BeFalse("there was no history of ours to lose");
        window.NextSeq.Should().Be(401);
    }

    [Fact]
    public void An_agent_restart_forces_the_ring_to_be_re_read_whole()
    {
        var restarted = Started.AddHours(2);
        var window = ApAgentEventCursorReader.Read(500, Started, Payload(restarted, false, 1, 3, 0, 1, 2, 3));

        window.RefetchFromStart.Should().BeTrue("a since= built on the old numbering can never be answered");
        window.AgentRestarted.Should().BeTrue();
        window.Gap.Should().BeTrue();
        window.NextSeq.Should().Be(0);
        window.Events.Should().BeEmpty("the reply answers a question about a run that no longer exists");
    }

    [Fact]
    public void A_start_time_that_moved_by_less_than_the_slack_is_not_a_restart()
    {
        var window = ApAgentEventCursorReader.Read(10, Started, Payload(Started.AddMilliseconds(400), false, 5, 12, 0, 11, 12));

        window.AgentRestarted.Should().BeFalse();
        window.RefetchFromStart.Should().BeFalse();
        window.NextSeq.Should().Be(12);
    }

    [Fact]
    public void A_ring_that_is_behind_the_cursor_is_re_read_whole()
    {
        var window = ApAgentEventCursorReader.Read(900, Started, Payload(Started, false, 1, 12, 0, 11, 12));

        window.RefetchFromStart.Should().BeTrue("the ring cannot be older than what we already consumed");
    }
}
