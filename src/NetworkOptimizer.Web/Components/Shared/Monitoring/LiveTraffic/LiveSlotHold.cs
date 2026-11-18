namespace NetworkOptimizer.Web.Components.Shared.Monitoring.LiveTraffic;

/// <summary>
/// How many rows a live card lays out: the most it has shown, so rows coming and going do not move the
/// cards below every refresh. Once fewer rows have shown for <see cref="SettleAfter"/>, it shrinks to
/// them in one step. A change the user made to the list (mode, sort, filters) starts over at once.
/// </summary>
public sealed class LiveSlotHold
{
    public static readonly TimeSpan SettleAfter = TimeSpan.FromSeconds(30);

    private string? _query;
    private int _slots;
    private DateTime? _fewerSince;

    /// <summary>The rows to lay out for <paramref name="shown"/> rows of <paramref name="query"/>.</summary>
    public int Rows(string query, int shown, DateTime now)
    {
        if (query != _query) (_query, _slots, _fewerSince) = (query, shown, null);
        if (shown >= _slots)
        {
            (_slots, _fewerSince) = (shown, null);
        }
        else
        {
            _fewerSince ??= now;
            if (now - _fewerSince >= SettleAfter) (_slots, _fewerSince) = (shown, null);
        }
        return _slots;
    }

    /// <summary>Starts over, as for a new client.</summary>
    public void Reset() => (_query, _slots, _fewerSince) = (null, 0, null);
}
