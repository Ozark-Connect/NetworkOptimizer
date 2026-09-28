using System.Buffers.Binary;
using System.Text;

namespace NetworkOptimizer.Monitoring.UciInform;

/// <summary>One complete inform body captured from a UCI.</summary>
/// <param name="SourceMac">Ethernet source of the frames, lowercase colon form.</param>
/// <param name="Body">The POST body: TNBU header plus encrypted payload.</param>
/// <param name="CapturedAt">When the last segment of the body arrived.</param>
public sealed record CapturedInform(string SourceMac, byte[] Body, DateTime CapturedAt);

/// <summary>
/// Rebuilds UniFi inform POSTs from raw ethernet frames. The capture only ever sees the UCI's
/// half of each connection (the BPF filter matches its source MAC), which is the half carrying
/// the request. Handles IPv4 and IPv6, 802.1Q tags, out-of-order and duplicated segments (a
/// frame seen on a bridge and on its member port arrives twice), and keep-alive connections
/// carrying several POSTs. Everything is bounded: a flow that grows past
/// <see cref="MaxFlowBytes"/>, stalls past <see cref="FlowIdleTimeout"/>, or loses sync is
/// dropped, never buffered indefinitely.
/// </summary>
public sealed class UciInformReassembler
{
    /// <summary>Largest request (headers + body) kept per flow.</summary>
    public const int MaxFlowBytes = 256 * 1024;

    /// <summary>Out-of-order segments held per flow while waiting for a gap to fill.</summary>
    public const int MaxPendingSegments = 64;

    /// <summary>Concurrent flows tracked; the stalest is evicted past this.</summary>
    public const int MaxFlows = 8;

    /// <summary>A flow with no new segment for this long is dropped.</summary>
    public static readonly TimeSpan FlowIdleTimeout = TimeSpan.FromSeconds(10);

    private const int TnbuHeaderLength = 40;
    private const int IvOffset = 16;
    private const int IvLength = 16;
    private const int RecentIvCapacity = 32;

    private static readonly byte[] PostPrefix = "POST "u8.ToArray();
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    private readonly Dictionary<FlowKey, Flow> _flows = new();
    private readonly LinkedList<string> _recentIvs = new();
    private readonly HashSet<string> _recentIvSet = new();

    /// <summary>Frames that parsed as TCP from a tracked MAC but were dropped (bounds, sync loss).</summary>
    public long DroppedFlows { get; private set; }

    /// <summary>
    /// Feeds one ethernet frame. Returns every inform body the frame completed (usually none,
    /// occasionally one).
    /// </summary>
    public IReadOnlyList<CapturedInform> OnFrame(ReadOnlySpan<byte> frame, DateTime now)
    {
        Sweep(now);
        if (!TryParseTcp(frame, out var srcMac, out var key, out var seq, out var payloadOffset, out var payloadLength))
            return Array.Empty<CapturedInform>();
        if (payloadLength == 0)
            return Array.Empty<CapturedInform>();

        var payload = frame.Slice(payloadOffset, payloadLength).ToArray();

        if (!_flows.TryGetValue(key, out var flow))
        {
            if (_flows.Count >= MaxFlows)
                EvictStalest();
            flow = new Flow(srcMac);
            _flows[key] = flow;
        }
        flow.LastSeen = now;

        Accept(flow, seq, payload);

        var completed = new List<CapturedInform>();
        if (!DrainRequests(flow, now, completed))
        {
            _flows.Remove(key);
            DroppedFlows++;
        }
        return completed;
    }

    /// <summary>Drops flows idle past <see cref="FlowIdleTimeout"/>.</summary>
    public void Sweep(DateTime now)
    {
        if (_flows.Count == 0) return;
        List<FlowKey>? stale = null;
        foreach (var (key, flow) in _flows)
        {
            if (now - flow.LastSeen > FlowIdleTimeout)
                (stale ??= new()).Add(key);
        }
        if (stale == null) return;
        foreach (var key in stale)
            _flows.Remove(key);
    }

    /// <summary>Flows currently tracked (for tests and diagnostics).</summary>
    public int FlowCount => _flows.Count;

    private void EvictStalest()
    {
        var stalest = _flows.MinBy(kv => kv.Value.LastSeen);
        _flows.Remove(stalest.Key);
        DroppedFlows++;
    }

    private static void Accept(Flow flow, uint seq, byte[] payload)
    {
        if (flow.NextSeq is not { } next)
        {
            // Not synced yet: a request starts at a segment beginning with "POST ". Anything
            // earlier (the tail of a request we joined mid-way) waits in pending until it is
            // either the start of one or ages out with the flow.
            flow.Pending[seq] = payload;
            TrimPending(flow, dropHighest: false);
            TrySync(flow);
            return;
        }

        var offset = unchecked((int)(seq - next));
        if (offset == 0)
        {
            Append(flow, payload);
        }
        else if (offset < 0)
        {
            // Retransmission or a duplicate capture. Keep only bytes past what we already have.
            var overlap = -offset;
            if (overlap < payload.Length)
                Append(flow, payload.AsSpan(overlap).ToArray());
        }
        else
        {
            flow.Pending[seq] = payload;
            TrimPending(flow, dropHighest: true);
        }
        DrainPending(flow);
    }

    /// <summary>
    /// Holds pending segments to <see cref="MaxPendingSegments"/> and <see cref="MaxFlowBytes"/>:
    /// a GRO-coalesced segment can be 64 KB, so a count alone would let a flow that never syncs
    /// hold megabytes on the gateway.
    /// </summary>
    private static void TrimPending(Flow flow, bool dropHighest)
    {
        while (flow.Pending.Count > 0
               && (flow.Pending.Count > MaxPendingSegments || flow.Pending.Values.Sum(s => (long)s.Length) > MaxFlowBytes))
        {
            flow.Pending.Remove(dropHighest ? flow.Pending.Keys.Max() : flow.Pending.Keys.First());
        }
    }

    private static void TrySync(Flow flow)
    {
        uint? start = null;
        foreach (var (seq, data) in flow.Pending)
        {
            if (data.AsSpan().StartsWith(PostPrefix) && (start == null || unchecked((int)(seq - start.Value)) < 0))
                start = seq;
        }
        if (start == null) return;

        // Segments before the request start belong to an earlier, unrecoverable request.
        foreach (var seq in flow.Pending.Keys.ToList())
        {
            if (unchecked((int)(seq - start.Value)) < 0)
                flow.Pending.Remove(seq);
        }
        flow.NextSeq = start;
        DrainPending(flow);
    }

    private static void Append(Flow flow, byte[] data)
    {
        flow.Buffer.Write(data);
        flow.NextSeq = unchecked(flow.NextSeq!.Value + (uint)data.Length);
    }

    private static void DrainPending(Flow flow)
    {
        var progressed = true;
        while (progressed && flow.NextSeq is { } next && flow.Pending.Count > 0)
        {
            progressed = false;
            foreach (var seq in flow.Pending.Keys.ToList())
            {
                var data = flow.Pending[seq];
                var offset = unchecked((int)(seq - next));
                if (offset > 0) continue;
                flow.Pending.Remove(seq);
                var overlap = -offset;
                if (overlap < data.Length)
                    Append(flow, data.AsSpan(overlap).ToArray());
                progressed = true;
                break;
            }
        }
    }

    /// <summary>Emits every complete request in the flow's buffer. False = the flow lost sync or overflowed.</summary>
    private bool DrainRequests(Flow flow, DateTime now, List<CapturedInform> completed)
    {
        while (flow.Buffer.Length > 0)
        {
            if (flow.Buffer.Length > MaxFlowBytes)
                return false;

            var buffer = flow.Buffer.GetBuffer().AsSpan(0, (int)flow.Buffer.Length);
            var prefixLength = Math.Min(buffer.Length, PostPrefix.Length);
            if (!buffer[..prefixLength].SequenceEqual(PostPrefix.AsSpan(0, prefixLength)))
                return false;

            var headerEnd = buffer.IndexOf(HeaderTerminator);
            if (headerEnd < 0)
                return true; // headers still arriving

            var headers = Encoding.ASCII.GetString(buffer[..headerEnd]);
            if (!IsInformRequest(headers) || !TryGetContentLength(headers, out var contentLength)
                || contentLength < 0 || contentLength > MaxFlowBytes)
                return false;

            var bodyStart = headerEnd + HeaderTerminator.Length;
            if (buffer.Length - bodyStart < contentLength)
                return true; // body still arriving

            var body = buffer.Slice(bodyStart, contentLength).ToArray();
            var consumed = bodyStart + contentLength;
            var rest = buffer[consumed..].ToArray();
            flow.Buffer.SetLength(0);
            flow.Buffer.Write(rest);

            if (body.Length >= TnbuHeaderLength && body.AsSpan(0, 4).SequenceEqual("TNBU"u8) && IsNewIv(body))
                completed.Add(new CapturedInform(flow.SourceMac, body, now));
        }
        return true;
    }

    private bool IsNewIv(byte[] body)
    {
        var iv = Convert.ToHexString(body, IvOffset, IvLength);
        if (!_recentIvSet.Add(iv)) return false;
        _recentIvs.AddLast(iv);
        if (_recentIvs.Count > RecentIvCapacity)
        {
            _recentIvSet.Remove(_recentIvs.First!.Value);
            _recentIvs.RemoveFirst();
        }
        return true;
    }

    private static bool IsInformRequest(string headers)
    {
        var firstLine = headers.Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ');
        if (parts.Length < 3) return false;
        var path = parts[1];
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        return path.EndsWith("/inform", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetContentLength(string headers, out int length)
    {
        length = -1;
        foreach (var line in headers.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(line.AsSpan(colon + 1).Trim(), out length);
        }
        return false;
    }

    /// <summary>
    /// Parses an ethernet frame down to its TCP payload. Handles one or two VLAN tags, IPv4 with
    /// options, and IPv6 without extension headers (the inform path uses none).
    /// </summary>
    internal static bool TryParseTcp(
        ReadOnlySpan<byte> frame, out string srcMac, out FlowKey key, out uint seq,
        out int payloadOffset, out int payloadLength)
    {
        srcMac = "";
        key = default;
        seq = 0;
        payloadOffset = 0;
        payloadLength = 0;

        if (frame.Length < 14) return false;
        srcMac = UciBpfFilter.FormatMac(frame.Slice(6, 6));
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(12, 2));
        var offset = 14;
        for (var tags = 0; tags < 2 && (etherType == 0x8100 || etherType == 0x88A8); tags++)
        {
            if (frame.Length < offset + 4) return false;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset + 2, 2));
            offset += 4;
        }

        string srcIp, dstIp;
        int tcpOffset, ipPayloadEnd;
        if (etherType == 0x0800)
        {
            if (frame.Length < offset + 20) return false;
            var ihl = (frame[offset] & 0x0F) * 4;
            if (ihl < 20 || frame[offset + 9] != 6) return false;
            var totalLength = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset + 2, 2));
            srcIp = new System.Net.IPAddress(frame.Slice(offset + 12, 4)).ToString();
            dstIp = new System.Net.IPAddress(frame.Slice(offset + 16, 4)).ToString();
            tcpOffset = offset + ihl;
            // A total length of 0 means TSO/GRO left it unset; trust the captured length then.
            ipPayloadEnd = totalLength == 0 ? frame.Length : Math.Min(frame.Length, offset + totalLength);
        }
        else if (etherType == 0x86DD)
        {
            if (frame.Length < offset + 40 || frame[offset + 6] != 6) return false;
            var payloadLen = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(offset + 4, 2));
            srcIp = new System.Net.IPAddress(frame.Slice(offset + 8, 16)).ToString();
            dstIp = new System.Net.IPAddress(frame.Slice(offset + 24, 16)).ToString();
            tcpOffset = offset + 40;
            ipPayloadEnd = payloadLen == 0 ? frame.Length : Math.Min(frame.Length, tcpOffset + payloadLen);
        }
        else
        {
            return false;
        }

        if (ipPayloadEnd < tcpOffset + 20) return false;
        var srcPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(tcpOffset, 2));
        var dstPort = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(tcpOffset + 2, 2));
        seq = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(tcpOffset + 4, 4));
        var dataOffset = (frame[tcpOffset + 12] >> 4) * 4;
        if (dataOffset < 20 || tcpOffset + dataOffset > ipPayloadEnd) return false;

        key = new FlowKey(srcIp, srcPort, dstIp, dstPort);
        payloadOffset = tcpOffset + dataOffset;
        payloadLength = ipPayloadEnd - payloadOffset;
        return true;
    }

    internal readonly record struct FlowKey(string SrcIp, ushort SrcPort, string DstIp, ushort DstPort);

    private sealed class Flow(string sourceMac)
    {
        public string SourceMac { get; } = sourceMac;
        public uint? NextSeq { get; set; }
        public MemoryStream Buffer { get; } = new();
        public Dictionary<uint, byte[]> Pending { get; } = new();
        public DateTime LastSeen { get; set; }
    }
}
