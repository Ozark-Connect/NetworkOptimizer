using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Detects brute force attempts: same source IP targeting the same destination port
/// (22/23/3389/443/8443) with 20+ events in 10 minutes.
/// </summary>
public class BruteForceDetector
{
    private const int MinEvents = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly HashSet<int> BruteForceTargetPorts = [22, 23, 3389, 443, 8443, 21, 25, 110, 143, 993, 995, 5900];

    public List<ThreatPattern> Detect(List<ThreatEvent> events)
    {
        var patterns = new List<ThreatPattern>();

        // Group by source IP and destination port
        var groups = events
            .Where(e => BruteForceTargetPorts.Contains(e.DestPort))
            .GroupBy(e => new { e.SourceIp, e.DestPort });

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(e => e.Timestamp).ToList();
            var windowStart = 0;

            for (var i = 0; i < ordered.Count; i++)
            {
                while (windowStart < i && ordered[i].Timestamp - ordered[windowStart].Timestamp > Window)
                    windowStart++;

                var windowCount = i - windowStart + 1;
                if (windowCount >= MinEvents)
                {
                    patterns.Add(new ThreatPattern
                    {
                        PatternType = PatternType.BruteForce,
                        DetectedAt = DateTime.UtcNow,
                        DedupKey = $"bf:{group.Key.SourceIp}:{group.Key.DestPort}",
                        SourceIpsJson = $"[\"{group.Key.SourceIp}\"]",
                        TargetPort = group.Key.DestPort,
                        EventCount = windowCount,
                        FirstSeen = ordered[windowStart].Timestamp,
                        LastSeen = ordered[i].Timestamp,
                        Confidence = Math.Min(1.0, windowCount / 50.0),
                        Description = $"Brute force from {group.Key.SourceIp} targeting port {group.Key.DestPort}: {windowCount} attempts in {Window.TotalMinutes}min"
                    });
                    break;
                }
            }
        }

        return patterns;
    }
}
