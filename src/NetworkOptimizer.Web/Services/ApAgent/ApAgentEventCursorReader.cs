using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One access point's new events, and what the cursor should become.</summary>
/// <param name="Events">Events after the stored sequence, in sequence order.</param>
/// <param name="NextSeq">The sequence to store, which never runs ahead of what was consumed.</param>
/// <param name="Gap">True when events were lost, so the preceding association is not to be trusted.</param>
/// <param name="AgentRestarted">True when the agent's numbering began again.</param>
/// <param name="RefetchFromStart">True when the reply cannot be used and the ring must be re-read whole.</param>
/// <param name="DroppedEvents">The ring's own count of events it overwrote.</param>
public sealed record ApAgentEventWindow(
    IReadOnlyList<ApAgentEvent> Events,
    long NextSeq,
    bool Gap,
    bool AgentRestarted,
    bool RefetchFromStart,
    long DroppedEvents);

/// <summary>
/// Turns a stored cursor and one /events reply into the events this server has not seen.
///
/// Two things make this more than a filter. The agent keeps no state across a restart, so its
/// sequence numbering begins again at 1 and a stored cursor then asks for a window that can never
/// arrive: that is detected from the agent's start time and answered by re-reading the ring whole.
/// And a ring that overwrote the requested window is reported rather than smoothed over, because
/// the association a roam is measured against may be one of the events that was lost.
/// </summary>
public static class ApAgentEventCursorReader
{
    /// <summary>Tolerance on the agent start time, which crosses JSON and two clocks.</summary>
    private static readonly TimeSpan StartTimeSlack = TimeSpan.FromSeconds(2);

    /// <summary>The sequence to ask for. Zero asks for the whole retained ring.</summary>
    public static long SinceFor(ApAgentEventCursor? cursor) => cursor?.LastSeq ?? 0;

    /// <summary>Reads one reply against the cursor state it was requested with.</summary>
    public static ApAgentEventWindow Read(long lastSeq, DateTime? knownStartedAt, ApAgentEventsPayload payload)
    {
        var dropped = payload.Ring?.Dropped ?? 0;
        var startedAt = payload.AgentStartedAt == default ? (DateTime?)null : payload.AgentStartedAt.ToUniversalTime();

        var restarted = knownStartedAt.HasValue && startedAt.HasValue
            && (startedAt.Value - knownStartedAt.Value).Duration() > StartTimeSlack;

        // Numbering restarted, or the ring is behind a cursor that outlived it: the reply answers a
        // question about a run that no longer exists, so it is re-read from the start instead.
        var newest = payload.Ring?.NewestSeq ?? 0;
        var rolledBack = lastSeq > 0 && newest > 0 && lastSeq > newest;
        if (lastSeq > 0 && (restarted || rolledBack))
            return new ApAgentEventWindow(Array.Empty<ApAgentEvent>(), 0, Gap: true, restarted, RefetchFromStart: true, dropped);

        var events = payload.Events
            .Where(e => (long)e.Seq > lastSeq)
            .OrderBy(e => e.Seq)
            .ToList();

        var next = events.Count > 0 ? (long)events[^1].Seq : lastSeq;

        // A first read has no history to have lost, so truncation there is not a gap in our data.
        var gap = payload.Truncated && lastSeq > 0;

        return new ApAgentEventWindow(events, next, gap, restarted, RefetchFromStart: false, dropped);
    }
}
