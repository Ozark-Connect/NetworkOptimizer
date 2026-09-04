using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// The last hour of noise floor readings per radio, in memory. A raised floor is a claim about
/// an hour, not a sample, so the rule that reports one reads the median here rather than the
/// latest value. Lost on restart, which only delays the first verdict by the hour it needs.
/// </summary>
public sealed class ApAgentNoiseFloorHistory
{
    /// <summary>How far back a median reaches.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Readings before a median is offered: 50 minutes at the 30 s radio cadence.</summary>
    public const int MinSamples = 100;

    private readonly ConcurrentDictionary<string, Queue<(DateTime At, int Floor)>> _byRadio = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records one reading. A floor at or above 0 dBm, or below -120, is a counter sentinel and is dropped.</summary>
    public void Record(string apMac, string radio, int? floorDbm, DateTime atUtc)
    {
        if (floorDbm is not (< 0 and > -120)) return;
        var queue = _byRadio.GetOrAdd(Key(apMac, radio), _ => new Queue<(DateTime, int)>());
        lock (queue)
        {
            queue.Enqueue((atUtc, floorDbm.Value));
            var cutoff = atUtc - Window;
            while (queue.Count > 0 && queue.Peek().At < cutoff)
                queue.Dequeue();
        }
    }

    /// <summary>The median floor over the window, or null until <see cref="MinSamples"/> readings exist.</summary>
    public int? HourMedian(string apMac, string radio, DateTime nowUtc)
    {
        if (!_byRadio.TryGetValue(Key(apMac, radio), out var queue)) return null;
        int[] floors;
        lock (queue)
        {
            var cutoff = nowUtc - Window;
            floors = queue.Where(s => s.At >= cutoff).Select(s => s.Floor).ToArray();
        }
        if (floors.Length < MinSamples) return null;
        Array.Sort(floors);
        return floors[floors.Length / 2];
    }

    /// <summary>Drops a radio's readings, for an access point whose agent has gone quiet.</summary>
    public void Forget(string apMac)
    {
        foreach (var key in _byRadio.Keys.Where(k => k.StartsWith(apMac + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            _byRadio.TryRemove(key, out _);
    }

    private static string Key(string apMac, string radio) => $"{apMac.Trim().ToLowerInvariant()}/{radio}";
}
