using System.Net;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// One parsed conntrack entry: the original and reply tuples with their byte counters.
/// <see cref="Key"/> is the flow's identity across samples - both tuples in full, so a NAT'd
/// flow and its hairpin twin can never collide. The trailing fields serve Live Traffic and are
/// zero or empty where the line does not carry them (ICMP has no ports; <c>delta-time</c> needs
/// <c>nf_conntrack_timestamp</c>).
/// </summary>
public sealed record ConntrackFlow(
    string Key,
    IPAddress OrigSrc,
    IPAddress OrigDst,
    IPAddress ReplySrc,
    IPAddress ReplyDst,
    long OrigBytes,
    long ReplyBytes,
    string Proto = "",
    int OrigSport = 0,
    int OrigDport = 0,
    long OrigPackets = 0,
    long ReplyPackets = 0,
    long AgeSeconds = -1);

/// <summary>
/// Parses <c>/proc/net/nf_conntrack</c>. One file read per sample pass, no exec; lines without
/// byte counters (nf_conntrack_acct off) or without a reply tuple are skipped - a flow that
/// cannot be accounted is not guessed at.
/// </summary>
public static class ConntrackParser
{
    /// <summary>
    /// Opens <paramref name="path"/> for reading through <paramref name="buffer"/>, which the caller keeps between
    /// passes: one read syscall per buffer's worth, as a FileStream with that buffer size makes, without allocating
    /// one. The file streams through the buffer, so memory stays fixed however large the table.
    /// </summary>
    public static Stream OpenBuffered(string path, byte[] buffer) => new ReusedBufferStream(
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0), buffer);

    private sealed class ReusedBufferStream(FileStream file, byte[] buffer) : Stream
    {
        private int _start, _end;

        public override int Read(byte[] destination, int offset, int count) => Read(destination.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = file.Read(buffer, 0, buffer.Length);
                if (_end == 0) return 0;
            }
            var n = Math.Min(destination.Length, _end - _start);
            buffer.AsSpan(_start, n).CopyTo(destination);
            _start += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] source, int offset, int count) => throw new NotSupportedException();
    }

    public static List<ConntrackFlow> Parse(TextReader reader)
    {
        var flows = new List<ConntrackFlow>();
        while (reader.ReadLine() is { } line)
        {
            if (ParseLine(line) is { } flow)
                flows.Add(flow);
        }
        return flows;
    }

    /// <summary>
    /// Parses one conntrack line, or null when it carries no accountable flow. The original
    /// tuple is everything up to the second <c>src=</c>, the reply tuple everything after;
    /// ports (or the ICMP id) ride into the key so reused tuples stay distinct.
    /// </summary>
    public static ConntrackFlow? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        IPAddress? origSrc = null, origDst = null, replySrc = null, replyDst = null;
        long? origBytes = null, replyBytes = null;
        long origPackets = 0, replyPackets = 0, ageSeconds = -1;
        int origSport = 0, origDport = 0;
        string origPorts = "", replyPorts = "";
        var inReply = false;
        string proto = "";

        // Tokenized on spans, not Split: this runs for every flow on every pass, and a string per token was
        // most of the agent's allocation.
        var rest = line.AsSpan();
        while (!rest.IsEmpty)
        {
            var space = rest.IndexOf(' ');
            var token = space < 0 ? rest : rest[..space];
            rest = space < 0 ? default : rest[(space + 1)..];
            if (token.IsEmpty) continue;

            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                // /proc layout: "<l3proto> <l3num> <l4proto> <l4num> [timeout] [state] key=value...";
                // `conntrack -E` layout: "[DESTROY] <l4proto> <l4num> key=value...". In both, the l4
                // protocol name is the first lowercase bare token that is not the l3 name - keyed
                // this way so a /proc snapshot and an event line produce the SAME flow key.
                if (proto.Length == 0 && token[0] != '['
                    && !token.SequenceEqual("ipv4") && !token.SequenceEqual("ipv6") && !char.IsAsciiDigit(token[0])
                    && char.IsAsciiLetterLower(token[0]))
                    proto = token.ToString();
                continue;
            }
            var name = token[..eq];
            var value = token[(eq + 1)..];
            switch (name)
            {
                case "src":
                    if (!IPAddress.TryParse(value, out var src)) return null;
                    if (origSrc == null) origSrc = src;
                    else if (!inReply) { inReply = true; replySrc = src; }
                    break;
                case "dst":
                    if (!IPAddress.TryParse(value, out var dst)) return null;
                    if (!inReply) origDst = dst;
                    else replyDst = dst;
                    break;
                case "sport" or "dport" or "id":
                    if (!inReply)
                    {
                        origPorts = string.Concat(origPorts, name, value);
                        if (name.SequenceEqual("sport")) int.TryParse(value, out origSport);
                        else if (name.SequenceEqual("dport")) int.TryParse(value, out origDport);
                    }
                    else replyPorts = string.Concat(replyPorts, name, value);
                    break;
                case "bytes":
                    if (!long.TryParse(value, out var bytes)) return null;
                    if (!inReply) origBytes = bytes;
                    else replyBytes = bytes;
                    break;
                case "packets":
                    if (long.TryParse(value, out var packets))
                    {
                        if (!inReply) origPackets = packets;
                        else replyPackets = packets;
                    }
                    break;
                case "delta-time":
                    long.TryParse(value, out ageSeconds);
                    break;
            }
        }

        if (origSrc == null || origDst == null || replySrc == null || replyDst == null) return null;
        if (origBytes == null || replyBytes == null) return null;

        var key = $"{proto}|{origSrc}|{origDst}|{origPorts}|{replySrc}|{replyDst}|{replyPorts}";
        return new ConntrackFlow(key, origSrc, origDst, replySrc, replyDst, origBytes.Value, replyBytes.Value,
            proto, origSport, origDport, origPackets, replyPackets, ageSeconds);
    }
}
