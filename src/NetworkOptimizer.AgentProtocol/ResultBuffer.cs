namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Store-and-forward buffer between the agent's collectors (probe and SNMP
/// runners) and the tunnel. Collectors always enqueue here - never directly
/// into a connection - so monitoring continues through tunnel outages and the
/// backlog replays in order once the tunnel reconnects. Bounded by sample age
/// and total serialized size; when either cap is exceeded the OLDEST messages
/// are dropped, since the newest data is the most valuable when the link
/// returns. Thread-safe for any number of producers and consumers.
/// </summary>
public sealed class ResultBuffer
{
    /// <summary>Oldest data worth replaying after an outage.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(12);

    /// <summary>
    /// Cap on total serialized message bytes. Sized so ~12 h of a typical
    /// agent site's probe + SNMP output fits with room to spare (see the
    /// per-sample estimates in the tunnel drain); a much larger site trims to
    /// proportionally fewer hours instead of growing without bound.
    /// </summary>
    public const long DefaultMaxBytes = 64 * 1024 * 1024;

    private readonly record struct Entry(AgentMessage Message, DateTime EnqueuedUtc, int SizeBytes);

    private readonly LinkedList<Entry> _entries = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _lock = new();
    private readonly TimeSpan _maxAge;
    private readonly long _maxBytes;
    private long _bytes;
    private long _dropped;
    private long _droppedUnreported;

    public ResultBuffer(TimeSpan? maxAge = null, long? maxBytes = null)
    {
        _maxAge = maxAge ?? DefaultMaxAge;
        _maxBytes = maxBytes ?? DefaultMaxBytes;
    }

    /// <summary>Number of buffered messages.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Total serialized size of the buffered messages.</summary>
    public long ApproxBytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary>Messages dropped by the age/size caps since construction.</summary>
    public long DroppedTotal
    {
        get { lock (_lock) return _dropped; }
    }

    /// <summary>
    /// Messages dropped since the last call, for periodic logging. Resets the
    /// unreported counter.
    /// </summary>
    public long TakeDroppedCount()
    {
        lock (_lock)
        {
            var count = _droppedUnreported;
            _droppedUnreported = 0;
            return count;
        }
    }

    /// <summary>Appends a message, evicting the oldest entries past the caps.</summary>
    public void Enqueue(AgentMessage message)
    {
        lock (_lock)
        {
            var entry = new Entry(message, DateTime.UtcNow, message.CalculateSize());
            _entries.AddLast(entry);
            _bytes += entry.SizeBytes;
            EvictLocked();
        }
        _available.Release();
    }

    /// <summary>
    /// Reinserts messages at the FRONT, preserving their order, so a failed
    /// send slots back in ahead of everything enqueued since. Entries get a
    /// fresh age stamp - they were dequeued seconds ago at most, so the age
    /// cap distortion is negligible.
    /// </summary>
    public void RequeueFront(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count == 0) return;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                var entry = new Entry(messages[i], now, messages[i].CalculateSize());
                _entries.AddFirst(entry);
                _bytes += entry.SizeBytes;
            }
            EvictLocked();
        }
        _available.Release(messages.Count);
    }

    /// <summary>
    /// Takes the oldest message, waiting until one is available. Throws
    /// <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public async ValueTask<AgentMessage> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            // Evictions and TryDequeueIf remove entries without consuming
            // permits, so a wake-up can find the list empty - loop and wait
            // for the next permit. Permits never undercount entries.
            await _available.WaitAsync(ct);
            lock (_lock)
            {
                if (_entries.Count > 0)
                    return TakeFirstLocked();
            }
        }
    }

    /// <summary>
    /// Takes the oldest message only if it satisfies <paramref name="predicate"/>.
    /// Used by the tunnel drain to coalesce a backlog of same-type batches.
    /// </summary>
    public bool TryDequeueIf(Func<AgentMessage, bool> predicate, out AgentMessage message)
    {
        lock (_lock)
        {
            if (_entries.Count > 0 && predicate(_entries.First!.Value.Message))
            {
                message = TakeFirstLocked();
                return true;
            }
        }
        message = null!;
        return false;
    }

    private AgentMessage TakeFirstLocked()
    {
        var entry = _entries.First!.Value;
        _entries.RemoveFirst();
        _bytes -= entry.SizeBytes;
        return entry.Message;
    }

    private void EvictLocked()
    {
        var cutoff = DateTime.UtcNow - _maxAge;
        while (_entries.Count > 0
               && (_bytes > _maxBytes || _entries.First!.Value.EnqueuedUtc < cutoff))
        {
            _bytes -= _entries.First!.Value.SizeBytes;
            _entries.RemoveFirst();
            _dropped++;
            _droppedUnreported++;
        }
    }
}
