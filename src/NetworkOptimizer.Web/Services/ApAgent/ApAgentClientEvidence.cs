using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>What the AP Agent knows about one association that the console never reports.</summary>
/// <param name="ApMac">The access point holding it, lowercase.</param>
/// <param name="ClientMac">The client key: MLD MAC when MLO, station MAC otherwise, lowercase.</param>
/// <param name="JoinSignal">Signal at authentication; null when the association predates the agent.</param>
/// <param name="AssociatedFor">How long the active link has been associated.</param>
/// <param name="RoamNudges">BSS transition requests this association answered.</param>
/// <param name="RoamNudgesAccepted">Of those, answers that accepted the transition.</param>
/// <param name="NegotiatedWidth">The width the client negotiated, in MHz.</param>
/// <param name="Nss">Operating spatial streams.</param>
/// <param name="At">When the reading was taken.</param>
/// <param name="MaxSupportedWidth">Widest channel the client supports, in MHz.</param>
public sealed record ApAgentClientFacts(
    string ApMac,
    string ClientMac,
    int? JoinSignal,
    TimeSpan? AssociatedFor,
    int? RoamNudges,
    int? RoamNudgesAccepted,
    int? NegotiatedWidth,
    int? Nss,
    DateTime At,
    int? MaxSupportedWidth = null);

/// <summary>
/// Per-association facts and the last hour of latency and stalls, from the sampling pass, held in
/// memory for the health rules. Per-association values are not a time series, so nothing here
/// reaches InfluxDB; the hour ring exists because a stall count is cumulative since association
/// and "in the last hour" needs the reading from an hour ago.
/// </summary>
public sealed class ApAgentClientEvidence
{
    /// <summary>How far back the latency and stall ring reaches.</summary>
    public static readonly TimeSpan HourWindow = TimeSpan.FromHours(1);

    /// <summary>A client not sampled for this long is forgotten.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    private sealed class Entry
    {
        public ApAgentClientFacts Facts = null!;
        public readonly List<(DateTime At, double? LatencyMs, long? Stalls)> Ring = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folds one sample in.</summary>
    public void Record(ApAgentWifiSample s, DateTime now)
    {
        var entry = _entries.GetOrAdd(Key(s.ApMac, s.ClientMac), _ => new Entry());
        lock (entry)
        {
            entry.Facts = new ApAgentClientFacts(
                s.ApMac, s.ClientMac,
                s.JoinSignal,
                s.AssocSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
                s.BtmRequests, s.BtmAccepted,
                s.ChannelWidth, s.Nss, now, s.MaxSupportedWidth);
            entry.Ring.Add((now, s.LatencyAvgMs, s.TcpStalls));
            var cutoff = now - HourWindow;
            entry.Ring.RemoveAll(r => r.At < cutoff);
        }
    }

    /// <summary>The clients one access point has sampled within retention.</summary>
    public IReadOnlyList<ApAgentClientFacts> Latest(string apMac, DateTime now)
    {
        var ap = ApAgentWifiFieldMapper.NormalizeMac(apMac);
        var list = new List<ApAgentClientFacts>();
        foreach (var entry in _entries.Values)
        {
            ApAgentClientFacts facts;
            lock (entry) facts = entry.Facts;
            if (facts.ApMac == ap && now - facts.At <= Retention) list.Add(facts);
        }
        return list;
    }

    /// <summary>
    /// The last hour for one client: the median of the AP's transmit latency readings, and how far
    /// the stall counter moved. Either is null without readings; a counter that went backwards
    /// (a reset) counts from zero.
    /// </summary>
    public (double? MedianLatencyMs, int? Stalls) HourStats(string apMac, string clientMac, DateTime now)
    {
        if (!_entries.TryGetValue(Key(apMac, clientMac), out var entry)) return (null, null);

        List<(DateTime At, double? LatencyMs, long? Stalls)> ring;
        lock (entry) ring = entry.Ring.Where(r => now - r.At <= HourWindow).ToList();
        if (ring.Count == 0) return (null, null);

        var latencies = ring.Where(r => r.LatencyMs.HasValue).Select(r => r.LatencyMs!.Value).OrderBy(v => v).ToList();
        double? median = latencies.Count == 0 ? null
            : latencies.Count % 2 == 1 ? latencies[latencies.Count / 2]
            : (latencies[latencies.Count / 2 - 1] + latencies[latencies.Count / 2]) / 2;

        var counted = ring.Where(r => r.Stalls.HasValue).ToList();
        int? stalls = null;
        if (counted.Count > 0)
        {
            var first = counted[0].Stalls!.Value;
            var last = counted[^1].Stalls!.Value;
            stalls = (int)Math.Clamp(last >= first ? last - first : last, 0, int.MaxValue);
        }
        return (median, stalls);
    }

    /// <summary>Forgets clients past retention.</summary>
    public void Prune(DateTime now)
    {
        foreach (var (key, entry) in _entries.ToList())
        {
            DateTime at;
            lock (entry) at = entry.Facts.At;
            if (now - at > Retention) _entries.TryRemove(key, out _);
        }
    }

    private static string Key(string apMac, string clientMac) =>
        $"{ApAgentWifiFieldMapper.NormalizeMac(apMac)}|{ApAgentWifiFieldMapper.NormalizeMac(clientMac)}";
}
