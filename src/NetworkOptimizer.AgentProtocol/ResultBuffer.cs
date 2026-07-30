using Google.Protobuf;

namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Store-and-forward buffer between the agent's collectors (probe and SNMP
/// runners) and the tunnel. Collectors always enqueue here - never directly
/// into a connection - so monitoring continues through tunnel outages and the
/// backlog replays in order once the tunnel reconnects.
///
/// A message stays in the buffer until the SERVER acknowledges it (see
/// <see cref="MarkAcked"/>), NOT when it is written to the socket. TCP reports a
/// write into a black-holed connection as success (the bytes sit in the kernel
/// send buffer and are discarded on teardown), so dropping a message on send
/// would silently lose an outage's worth of data the moment the link goes dead.
/// The drain therefore only ever PEEKS unsent entries
/// (<see cref="TakeUnsentBatchAsync"/>); an unacked frame is re-sent on the next
/// connection.
///
/// Bounded by sample age and total serialized size; when either cap is exceeded
/// the OLDEST messages are dropped, since the newest data is the most valuable
/// when the link returns - this is the only way a message leaves the buffer
/// without being acked. Thread-safe for any number of producers and consumers.
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

    /// <summary>One buffered message and the monotonic sequence assigned at enqueue.</summary>
    private readonly record struct Entry(long Seq, AgentMessage Message, DateTime EnqueuedUtc, int SizeBytes);

    /// <summary>A coalesced frame ready to send, tagged with the highest sequence it covers.</summary>
    public sealed record SendFrame(AgentMessage Message, long ThroughSeq);

    private readonly LinkedList<Entry> _entries = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _lock = new();
    private readonly TimeSpan _maxAge;
    private readonly long _maxBytes;
    private long _bytes;
    private long _nextSeq;
    private long _dropped;
    private long _droppedUnreported;

    public ResultBuffer(TimeSpan? maxAge = null, long? maxBytes = null)
    {
        _maxAge = maxAge ?? DefaultMaxAge;
        _maxBytes = maxBytes ?? DefaultMaxBytes;
    }

    /// <summary>Number of buffered (unacked) messages.</summary>
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

    /// <summary>
    /// Appends a message with the next sequence number, evicting the oldest
    /// entries past the caps.
    /// </summary>
    public void Enqueue(AgentMessage message)
    {
        lock (_lock)
        {
            var entry = new Entry(++_nextSeq, message, DateTime.UtcNow, message.CalculateSize());
            _entries.AddLast(entry);
            _bytes += entry.SizeBytes;
            EvictLocked();
        }
        _available.Release();
    }

    /// <summary>
    /// Peeks the oldest run of entries whose sequence is greater than
    /// <paramref name="afterSeq"/>, coalescing consecutive same-type batches up
    /// to <paramref name="maxSamples"/>, and returns them as one frame tagged
    /// with the highest sequence it covers. Entries are NOT removed - they stay
    /// buffered until <see cref="MarkAcked"/>. Waits until such an entry exists;
    /// throws <see cref="OperationCanceledException"/> on cancellation.
    /// A caller re-sending after a reconnect passes afterSeq = 0 to replay every
    /// unacked entry from the oldest.
    /// </summary>
    public async ValueTask<SendFrame> TakeUnsentBatchAsync(long afterSeq, int maxSamples, CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                var frame = BuildUnsentFrameLocked(afterSeq, maxSamples);
                if (frame != null)
                    return frame;
            }
            await _available.WaitAsync(ct);
            // Evictions and coalescing consume entries without matching permits,
            // so drain any surplus and re-check under the lock. The synchronous
            // BuildUnsentFrameLocked above is the source of truth; the permit is
            // only a wake-up, so an over- or under-count never loses a frame.
            while (_available.Wait(0)) { }
        }
    }

    /// <summary>
    /// Removes every buffered entry whose sequence is at or below
    /// <paramref name="throughSeq"/> - the server's cumulative acknowledgement
    /// that those frames are persisted. Sequences are monotonic and entries are
    /// ordered, so the acked run is always at the front.
    /// </summary>
    public void MarkAcked(long throughSeq)
    {
        lock (_lock)
        {
            while (_entries.First is { } first && first.Value.Seq <= throughSeq)
            {
                _bytes -= first.Value.SizeBytes;
                _entries.RemoveFirst();
            }
        }
    }

    /// <summary>Spool file header: identifies the format and its version.</summary>
    private const uint SpoolMagic = 0x4E4F5350; // "NOSP"
    private const int SpoolVersion = 1;

    /// <summary>
    /// Writes every buffered (unacked) message with its enqueue time to
    /// <paramref name="path"/>, replacing any existing file, so a restart
    /// (deliberate stop, update, or watchdog-forced) keeps the outage backlog
    /// instead of losing it with the process. Deliberately synchronous file
    /// I/O: the async-engine watchdog calls this while async I/O is wedged.
    /// </summary>
    public void SaveTo(string path)
    {
        lock (_lock)
        {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            var output = new CodedOutputStream(file);
            output.WriteFixed32(SpoolMagic);
            output.WriteInt32(SpoolVersion);
            foreach (var entry in _entries)
            {
                output.WriteInt64(entry.EnqueuedUtc.Ticks);
                output.WriteMessage(entry.Message);
            }
            output.Flush();
        }
    }

    /// <summary>
    /// Appends the messages spooled by <see cref="SaveTo"/>, preserving their
    /// original enqueue times so the age cap still applies, then re-applies the
    /// caps. A truncated or corrupt spool (crash mid-write) keeps whatever
    /// decoded cleanly. Returns the number of messages restored.
    /// </summary>
    public int LoadFrom(string path)
    {
        using var file = File.OpenRead(path);
        var input = new CodedInputStream(file);
        var restored = 0;
        lock (_lock)
        {
            try
            {
                if (input.ReadFixed32() != SpoolMagic || input.ReadInt32() != SpoolVersion)
                    return 0;
                while (!input.IsAtEnd)
                {
                    var enqueuedTicks = input.ReadInt64();
                    var message = new AgentMessage();
                    input.ReadMessage(message);
                    var entry = new Entry(++_nextSeq, message,
                        new DateTime(enqueuedTicks, DateTimeKind.Utc), message.CalculateSize());
                    _entries.AddLast(entry);
                    _bytes += entry.SizeBytes;
                    restored++;
                }
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or ArgumentOutOfRangeException)
            {
                // Truncated tail or corrupt bytes (crash mid-write, bit rot):
                // keep the clean prefix. ArgumentOutOfRange covers a corrupt
                // varint decoding to ticks outside DateTime's range.
            }
            EvictLocked();
        }
        if (restored > 0)
            _available.Release();
        return restored;
    }

    private SendFrame? BuildUnsentFrameLocked(long afterSeq, int maxSamples)
    {
        var node = _entries.First;
        while (node != null && node.Value.Seq <= afterSeq)
            node = node.Next;
        if (node == null)
            return null;

        // Clone so the buffered originals stay intact for a possible re-send;
        // coalescing must never mutate what is still awaiting an ack.
        var merged = node.Value.Message.Clone();
        var throughSeq = node.Value.Seq;
        node = node.Next;

        if (merged.PayloadCase == AgentMessage.PayloadOneofCase.ProbeResults)
        {
            while (node != null
                   && node.Value.Message.PayloadCase == AgentMessage.PayloadOneofCase.ProbeResults
                   && merged.ProbeResults.Results.Count < maxSamples)
            {
                merged.ProbeResults.Results.AddRange(node.Value.Message.ProbeResults.Results);
                throughSeq = node.Value.Seq;
                node = node.Next;
            }
        }
        else if (merged.PayloadCase == AgentMessage.PayloadOneofCase.SnmpResults)
        {
            int Samples() => merged.SnmpResults.Interfaces.Count
                             + merged.SnmpResults.Health.Count
                             + merged.SnmpResults.CustomOids.Count;
            while (node != null
                   && node.Value.Message.PayloadCase == AgentMessage.PayloadOneofCase.SnmpResults
                   && Samples() < maxSamples)
            {
                merged.SnmpResults.Interfaces.AddRange(node.Value.Message.SnmpResults.Interfaces);
                merged.SnmpResults.Health.AddRange(node.Value.Message.SnmpResults.Health);
                merged.SnmpResults.CustomOids.AddRange(node.Value.Message.SnmpResults.CustomOids);
                throughSeq = node.Value.Seq;
                node = node.Next;
            }
        }

        return new SendFrame(merged, throughSeq);
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
