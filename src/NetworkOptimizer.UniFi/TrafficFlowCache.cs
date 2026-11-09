using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// The console's recent traffic-flows records, kept per <see cref="UniFiApiClient"/> so its readers share
/// them: Threat Intelligence's collector reads every record each minute, and Live Traffic's views open
/// from here instead of reading the last hour again. On a Cloud Gateway the console is the gateway, so
/// every repeated read is load on it.
/// Only a complete, unfiltered read (every page, to a short one) is recorded, and the span it covers is
/// tracked: a reader may rely on the records only inside <see cref="Covered"/>.
/// </summary>
public sealed class TrafficFlowCache
{
    /// <summary>How long a record is kept: the History window, plus slack for a reader's overlap.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(65);

    private readonly object _gate = new();
    private readonly Dictionary<string, UniFiTrafficFlow> _byId = new(StringComparer.Ordinal);
    private DateTimeOffset? _coveredFrom;
    private DateTimeOffset? _coveredTo;

    /// <summary>
    /// Records one complete, unfiltered read of [<paramref name="from"/>, <paramref name="to"/>]. A read
    /// that meets or overlaps the covered span extends it; one with a gap before it starts a new span.
    /// </summary>
    public void Record(DateTimeOffset from, DateTimeOffset to, IEnumerable<UniFiTrafficFlow> records, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var r in records)
                if (!string.IsNullOrEmpty(r.Id)) _byId[r.Id] = r;

            if (_coveredTo is { } coveredTo && _coveredFrom is { } coveredFrom && from <= coveredTo && to >= coveredFrom)
            {
                _coveredFrom = from < coveredFrom ? from : coveredFrom;
                _coveredTo = to > coveredTo ? to : coveredTo;
            }
            else if (_coveredTo == null || to > _coveredTo)
            {
                _coveredFrom = from;
                _coveredTo = to;
            }
            Prune(now);
        }
    }

    /// <summary>The span complete reads have covered, or null before the first one.</summary>
    public (DateTimeOffset From, DateTimeOffset To)? Covered
    {
        get
        {
            lock (_gate)
                return _coveredFrom is { } f && _coveredTo is { } t ? (f, t) : null;
        }
    }

    /// <summary>The records whose last report is at or after <paramref name="since"/>.</summary>
    public List<UniFiTrafficFlow> Since(DateTimeOffset since)
    {
        var ms = since.ToUnixTimeMilliseconds();
        lock (_gate)
            return _byId.Values.Where(r => LastSeen(r) >= ms).ToList();
    }

    /// <summary>Forgets everything, for a console that changed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byId.Clear();
            _coveredFrom = _coveredTo = null;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Keep;
        var ms = cutoff.ToUnixTimeMilliseconds();
        foreach (var id in _byId.Where(kv => LastSeen(kv.Value) < ms).Select(kv => kv.Key).ToList())
            _byId.Remove(id);
        if (_coveredFrom is { } f && f < cutoff) _coveredFrom = cutoff;
        if (_coveredTo is { } t && _coveredFrom is { } f2 && t < f2) _coveredFrom = _coveredTo = null;
    }

    private static long LastSeen(UniFiTrafficFlow r) => Math.Max(r.Time, Math.Max(r.FlowEndTime, r.FlowStartTime));
}
