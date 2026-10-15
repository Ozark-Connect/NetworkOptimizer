using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Detects port scan sweeps: same source IP targeting 5+ distinct destination ports within 6 hours.
/// Includes both Reconnaissance and AttemptedExploitation stages (blocked probes to sensitive ports
/// like SSH/Telnet are still scanning behavior).
/// </summary>
public class ScanSweepDetector
{
    private const int MinDistinctPorts = 5;
    private static readonly TimeSpan Window = TimeSpan.FromHours(6);

    public List<ThreatPattern> Detect(List<ThreatEvent> events)
    {
        var patterns = new List<ThreatPattern>();

        // Group by source IP - include both recon and attempted exploitation (blocked probes to
        // sensitive ports are still scanning)
        var bySource = events
            .Where(e => e.KillChainStage is KillChainStage.Monitored or KillChainStage.Reconnaissance or KillChainStage.AttemptedExploitation)
            .Where(e => !IsNotAProbe(e))
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
                        DedupKey = $"ss:{group.Key}",
                        SourceIpsJson = $"[\"{group.Key}\"]",
                        EventCount = windowEvents.Count,
                        FirstSeen = windowEvents.First().Timestamp,
                        LastSeen = windowEvents.Last().Timestamp,
                        Confidence = Math.Min(1.0, distinctPorts / 15.0),
                        Description = $"Port scan from {group.Key}: {distinctPorts} ports targeted in {Window.TotalHours}h"
                    });
                    break; // One pattern per source IP per analysis run
                }
            }
        }

        return patterns;
    }

    /// <summary>Server ports that late replies to LAN clients come from.</summary>
    private static readonly HashSet<int> ServerPorts = [53, 80, 123, 443, 853, 993, 3478, 5222, 5223, 5228, 5349];

    /// <summary>The ephemeral range a client's side of a connection sits in (Linux starts at 32768, Windows at 49152).</summary>
    private const int EphemeralPortFloor = 32768;

    /// <summary>
    /// Traffic that hits many ports without being a scan: a server's late replies to a LAN client
    /// (server port to an ephemeral port), and UDP traceroute probes (33434-33534). Both reach the
    /// distinct-port threshold on their own and blocked CDNs and upstream routers.
    /// </summary>
    public static bool IsNotAProbe(ThreatEvent e)
    {
        if (ServerPorts.Contains(e.SourcePort) && e.DestPort >= EphemeralPortFloor)
            return true;
        return e.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) && e.DestPort is >= 33434 and <= 33534;
    }
}
