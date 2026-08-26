namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Turns an agent-observed membership change into a hint that the Console's client list is worth
/// re-reading. Lazy on purpose: nothing here polls the Console. Consumers holding a roster cache
/// compare their snapshot against the hint on their own next read, so an unwatched site costs the
/// Console nothing at all.
/// </summary>
public sealed class ConsoleRosterNudge
{
    /// <summary>
    /// How long the Console gets to digest the change before a re-read is suggested. It learns of
    /// an association on its own clock, so re-reading immediately returns the stale roster.
    /// </summary>
    public static readonly TimeSpan ConsoleSettleDelay = TimeSpan.FromSeconds(10);

    /// <summary>Floor between suggested re-reads, so roam churn cannot become a continuous poll.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private DateTime? _dueAt;
    private DateTime _lastDueAt = DateTime.MinValue;

    /// <summary>
    /// Notes that an access point's membership changed. Coalesced: while a suggestion is pending,
    /// a further change can only pull it earlier, never push it out. Immediate skips the settle
    /// delay - right for a departure, where the Console still lists the client and the presence
    /// gate excludes it regardless, so there is nothing to wait for. The floor holds either way.
    /// </summary>
    public void NoteMembershipChange(DateTime now, bool immediate = false)
    {
        lock (_lock)
        {
            var due = immediate ? now : now + ConsoleSettleDelay;
            var floor = _lastDueAt + MinInterval;
            if (due < floor) due = floor;

            if (_dueAt is { } pending && due >= pending) return;
            _dueAt = due;
        }
    }

    /// <summary>
    /// Whether a roster snapshot taken at <paramref name="snapshotAt"/> should be re-read now.
    /// True once the pending suggestion has come due, for every consumer whose snapshot predates
    /// it; a snapshot taken after the due instant is already the fresh one.
    /// </summary>
    public bool ShouldRefresh(DateTime snapshotAt, DateTime now)
    {
        lock (_lock)
        {
            if (_dueAt is { } due && now >= due)
            {
                _lastDueAt = due;
                _dueAt = null;
            }
            return snapshotAt < _lastDueAt;
        }
    }
}
