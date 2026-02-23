using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class ThreatPatternAnalyzerTests
{
    private readonly ThreatPatternAnalyzer _analyzer;

    public ThreatPatternAnalyzerTests()
    {
        var logger = new Mock<ILogger<ThreatPatternAnalyzer>>();
        _analyzer = new ThreatPatternAnalyzer(logger.Object);
    }

    [Fact]
    public void DetectPatterns_EmptyList_ReturnsEmpty()
    {
        var result = _analyzer.DetectPatterns([]);

        Assert.Empty(result);
    }

    [Fact]
    public void DetectPatterns_ScanSweepEvents_DetectsPortScan()
    {
        var events = new List<ThreatEvent>();
        var now = DateTime.UtcNow;

        // One IP targeting 15 different ports within an hour
        for (var port = 1; port <= 15; port++)
        {
            events.Add(new ThreatEvent
            {
                SourceIp = "198.51.100.1",
                DestIp = "192.0.2.1",
                DestPort = port,
                SignatureId = 1000,
                SignatureName = "Port probe",
                Timestamp = now.AddMinutes(-port),
                Action = ThreatAction.Blocked,
                KillChainStage = KillChainStage.Reconnaissance
            });
        }

        var patterns = _analyzer.DetectPatterns(events);

        Assert.Contains(patterns, p => p.PatternType == PatternType.ScanSweep);
    }

    [Fact]
    public void DetectPatterns_BruteForceEvents_DetectsBruteForce()
    {
        var events = new List<ThreatEvent>();
        var now = DateTime.UtcNow;

        // Same IP targeting SSH (port 22) with 25 events in 5 minutes
        for (var i = 0; i < 25; i++)
        {
            events.Add(new ThreatEvent
            {
                SourceIp = "198.51.100.1",
                DestIp = "192.0.2.1",
                DestPort = 22,
                SignatureId = 2000,
                SignatureName = "SSH brute force",
                Timestamp = now.AddSeconds(-i * 10), // 10s apart = 250s < 10min
                Action = ThreatAction.Blocked,
                KillChainStage = KillChainStage.AttemptedExploitation
            });
        }

        var patterns = _analyzer.DetectPatterns(events);

        Assert.Contains(patterns, p => p.PatternType == PatternType.BruteForce);
    }

    [Fact]
    public void DetectPatterns_NoPatternEvents_ReturnsEmpty()
    {
        var events = new List<ThreatEvent>
        {
            new()
            {
                SourceIp = "198.51.100.1",
                DestIp = "192.0.2.1",
                DestPort = 443,
                SignatureId = 1000,
                SignatureName = "Single event",
                Timestamp = DateTime.UtcNow,
                Action = ThreatAction.Detected,
                KillChainStage = KillChainStage.Reconnaissance
            }
        };

        var patterns = _analyzer.DetectPatterns(events);

        Assert.Empty(patterns);
    }
}
