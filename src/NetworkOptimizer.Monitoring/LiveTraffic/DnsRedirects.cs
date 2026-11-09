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
        {
            if (r.ClientPort == 0 || !redirects.TryGetValue((r.ClientIp, r.ClientPort), out var d))
            {
                yield return (r, "");
                continue;
            }
            if (r.ServerIp == d.Asked) yield return (r, d.Answered);
            else if (r.ServerIp != d.Answered) yield return (r, "");
        }
    }
}
