using NetworkOptimizer.Monitoring.Conntrack;

namespace NetworkOptimizer.Monitoring.LiveTraffic;

/// <summary>
/// DNS lookups the Gateway redirected (DNAT) to another resolver: conntrack keeps the address the
/// client sent to in the original tuple and the one that answered in the reply tuple. A redirected
/// lookup is captured twice, the client's own leg and the redirected leg on the resolver's network;
/// only the client's is kept, marked with the resolver that answered.
/// </summary>
public static class DnsRedirects
{
    /// <summary>(client, client port) to (the server it asked, the resolver that answered), for port-53 flows that were redirected.</summary>
    public static Dictionary<(string Client, int Port), (string Asked, string Answered)> From(IEnumerable<ConntrackFlow> flows)
    {
        var map = new Dictionary<(string, int), (string, string)>();
        foreach (var f in flows)
        {
            if (f.OrigDport != 53 || f.OrigSport == 0 || f.OrigDst.Equals(f.ReplySrc)) continue;
            map[(f.OrigSrc.ToString(), f.OrigSport)] = (f.OrigDst.ToString(), f.ReplySrc.ToString());
        }
        return map;
    }

    /// <summary>
    /// Each lookup with the resolver that answered it when it was redirected, else empty. The
    /// redirected leg of a lookup already listed from the client's side is left out.
    /// </summary>
    public static IEnumerable<(LiveDnsRecord Record, string RedirectedTo)> Apply(
        IEnumerable<LiveDnsRecord> records, IReadOnlyDictionary<(string Client, int Port), (string Asked, string Answered)> redirects)
    {
        foreach (var r in records)
            if (Resolve(r, redirects) is { } result) yield return result;
    }

    /// <summary>
    /// How far before the snapshot a lookup can still be missing from it: the capture sees a query
    /// just before netfilter creates its entry.
    /// </summary>
    private static readonly TimeSpan SnapshotSkew = TimeSpan.FromSeconds(1);

    /// <summary>
    /// As <see cref="Apply(IEnumerable{LiveDnsRecord}, IReadOnlyDictionary{ValueTuple{string, int}, ValueTuple{string, string}})"/>,
    /// against a conntrack snapshot read from <paramref name="snapshotAt"/>. A lookup sent after it has
    /// no entry there yet, so a fresh one that does not match goes to <paramref name="later"/> for the
    /// next pass instead of reading as not redirected. <paramref name="held"/> are last pass's: never held twice.
    /// </summary>
    public static IEnumerable<(LiveDnsRecord Record, string RedirectedTo)> Apply(
        IReadOnlyList<LiveDnsRecord> held, IReadOnlyList<LiveDnsRecord> fresh,
        IReadOnlyDictionary<(string Client, int Port), (string Asked, string Answered)> redirects,
        DateTime snapshotAt, List<LiveDnsRecord> later)
    {
        foreach (var r in held)
            if (Resolve(r, redirects) is { } result) yield return result;
        foreach (var r in fresh)
        {
            if (r.ClientPort != 0 && r.At >= snapshotAt - SnapshotSkew && !redirects.ContainsKey((r.ClientIp, r.ClientPort)))
            {
                later.Add(r);
                continue;
            }
            if (Resolve(r, redirects) is { } result) yield return result;
        }
    }

    /// <summary>The lookup with the resolver that answered it, or null for a redirected leg already listed from the client's side.</summary>
    private static (LiveDnsRecord, string)? Resolve(
        LiveDnsRecord r, IReadOnlyDictionary<(string Client, int Port), (string Asked, string Answered)> redirects)
    {
        if (r.ClientPort == 0 || !redirects.TryGetValue((r.ClientIp, r.ClientPort), out var d)) return (r, "");
        if (r.ServerIp == d.Asked) return (r, d.Answered);
        return r.ServerIp != d.Answered ? (r, "") : null;
    }
}
