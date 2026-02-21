using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Orchestrates all pattern detectors against a set of threat events.
/// </summary>
public class ThreatPatternAnalyzer
{
    private readonly ILogger<ThreatPatternAnalyzer> _logger;
    private readonly ScanSweepDetector _scanDetector = new();
    private readonly BruteForceDetector _bruteForceDetector = new();
    private readonly ExploitCampaignDetector _exploitDetector = new();
    private readonly DDoSDetector _ddosDetector = new();

    public ThreatPatternAnalyzer(ILogger<ThreatPatternAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run all detectors and return newly detected patterns.
    /// </summary>
    public List<ThreatPattern> DetectPatterns(List<ThreatEvent> events)
    {
        if (events.Count == 0) return [];

        var patterns = new List<ThreatPattern>();

        try { patterns.AddRange(_scanDetector.Detect(events)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Scan sweep detection failed"); }

        try { patterns.AddRange(_bruteForceDetector.Detect(events)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Brute force detection failed"); }

        try { patterns.AddRange(_exploitDetector.Detect(events)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Exploit campaign detection failed"); }

        try { patterns.AddRange(_ddosDetector.Detect(events)); }
        catch (Exception ex) { _logger.LogWarning(ex, "DDoS detection failed"); }

        if (patterns.Count > 0)
            _logger.LogInformation("Detected {Count} attack patterns from {Events} events",
                patterns.Count, events.Count);

        return patterns;
    }
}
