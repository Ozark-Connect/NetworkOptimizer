using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Detects port scan sweeps: same source IP targeting 10+ distinct destination ports within 1 hour.
/// </summary>
public class ScanSweepDetector
{
    private const int MinDistinctPorts = 10;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    public List<ThreatPattern> Detect(List<ThreatEvent> events)
    {
        var patterns = new List<ThreatPattern>();

        // Group by source IP
        var bySource = events
            .Where(e => e.KillChainStage == KillChainStage.Reconnaissance)
            .GroupBy(e => e.SourceIp);

        foreach (var group in bySource)
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToList();
            var windowStart = 0;

            for (var i = 0; i < ordered.Count; i++)
            {
                // Slide window start forward
                while (windowStart < i && ordered[i].Timestamp - ordered[windowStart].Timestamp > Window)
                    windowStart++;

                var windowEvents = ordered.Skip(windowStart).Take(i - windowStart + 1).ToList();
                var distinctPorts = windowEvents.Select(e => e.DestPort).Distinct().Count();

                if (distinctPorts >= MinDistinctPorts)
                {
                    patterns.Add(new ThreatPattern
                    {
                        PatternType = PatternType.ScanSweep,
                        DetectedAt = DateTime.UtcNow,
                        SourceIpsJson = $"[\"{group.Key}\"]",
                        EventCount = windowEvents.Count,
                        FirstSeen = windowEvents.First().Timestamp,
                        LastSeen = windowEvents.Last().Timestamp,
                        Confidence = Math.Min(1.0, distinctPorts / 20.0),
                        Description = $"Port scan from {group.Key}: {distinctPorts} ports targeted in {Window.TotalMinutes}min"
                    });
                    break; // One pattern per source IP per analysis run
                }
            }
        }

        return patterns;
    }
}
