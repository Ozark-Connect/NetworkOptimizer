using System.Text.Json;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Detects DDoS patterns: same destination IP + port receiving 100+ events
/// from 10+ unique sources within 5 minutes.
/// </summary>
public class DDoSDetector
{
    private const int MinEvents = 100;
    private const int MinUniqueSources = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public List<ThreatPattern> Detect(List<ThreatEvent> events)
    {
        var patterns = new List<ThreatPattern>();

        // Group by destination IP + port
        var groups = events
            .GroupBy(e => new { e.DestIp, e.DestPort });

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToList();
            if (ordered.Count < MinEvents) continue;

            var windowStart = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                while (windowStart < i && ordered[i].Timestamp - ordered[windowStart].Timestamp > Window)
                    windowStart++;

                var windowCount = i - windowStart + 1;
                if (windowCount >= MinEvents)
                {
                    var windowEvents = ordered.Skip(windowStart).Take(windowCount).ToList();
                    var uniqueSources = windowEvents.Select(e => e.SourceIp).Distinct().Count();

                    if (uniqueSources >= MinUniqueSources)
                    {
                        var sourceIps = windowEvents.Select(e => e.SourceIp).Distinct().Take(20).ToList();
                        patterns.Add(new ThreatPattern
                        {
                            PatternType = PatternType.DDoS,
                            DetectedAt = DateTime.UtcNow,
                            SourceIpsJson = JsonSerializer.Serialize(sourceIps),
                            TargetPort = group.Key.DestPort,
                            EventCount = windowCount,
                            FirstSeen = windowEvents.First().Timestamp,
                            LastSeen = windowEvents.Last().Timestamp,
                            Confidence = Math.Min(1.0, (double)uniqueSources / 50),
                            Description = $"DDoS targeting {group.Key.DestIp}:{group.Key.DestPort}: {windowCount} events from {uniqueSources} sources in {Window.TotalMinutes}min"
                        });
                        break;
                    }
                }
            }
        }

        return patterns;
    }
}
