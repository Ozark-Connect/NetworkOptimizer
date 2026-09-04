using System.Net;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// One parsed conntrack entry: the original and reply tuples with their byte counters.
/// <see cref="Key"/> is the flow's identity across samples - both tuples in full, so a NAT'd
/// flow and its hairpin twin can never collide.
/// </summary>
public sealed record ConntrackFlow(
    string Key,
    IPAddress OrigSrc,
    IPAddress OrigDst,
    IPAddress ReplySrc,
    IPAddress ReplyDst,
    long OrigBytes,
    long ReplyBytes);

/// <summary>
/// Parses <c>/proc/net/nf_conntrack</c>. One file read per sample pass, no exec; lines without
/// byte counters (nf_conntrack_acct off) or without a reply tuple are skipped - a flow that
/// cannot be accounted is not guessed at.
/// </summary>
public static class ConntrackParser
{
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
        string origPorts = "", replyPorts = "";
        var inReply = false;
        string proto = "";

        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                // /proc layout: "<l3proto> <l3num> <l4proto> <l4num> [timeout] [state] key=value...";
                // `conntrack -E` layout: "[DESTROY] <l4proto> <l4num> key=value...". In both, the l4
                // protocol name is the first lowercase bare token that is not the l3 name - keyed
                // this way so a /proc snapshot and an event line produce the SAME flow key.
                if (proto.Length == 0 && token[0] != '['
                    && token != "ipv4" && token != "ipv6" && !char.IsAsciiDigit(token[0])
                    && char.IsAsciiLetterLower(token[0]))
                    proto = token;
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
                    if (!inReply) origPorts += name + value;
                    else replyPorts += name + value;
                    break;
                case "bytes":
                    if (!long.TryParse(value, out var bytes)) return null;
                    if (!inReply) origBytes = bytes;
                    else replyBytes = bytes;
                    break;
            }
        }

        if (origSrc == null || origDst == null || replySrc == null || replyDst == null) return null;
        if (origBytes == null || replyBytes == null) return null;

        var key = $"{proto}|{origSrc}|{origDst}|{origPorts}|{replySrc}|{replyDst}|{replyPorts}";
        return new ConntrackFlow(key, origSrc, origDst, replySrc, replyDst, origBytes.Value, replyBytes.Value);
    }
}
