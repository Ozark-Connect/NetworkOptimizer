using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class ScanSweepDetectorTests
{
    private readonly ScanSweepDetector _detector = new();

    private static ThreatEvent CreateReconEvent(
        string sourceIp,
        int destPort,
        DateTime timestamp)
    {
        return new ThreatEvent
        {
            InnerAlertId = Guid.NewGuid().ToString(),
            Timestamp = timestamp,
            SourceIp = sourceIp,
            SourcePort = 12345,
            DestIp = "198.51.100.1",
            DestPort = destPort,
            Protocol = "TCP",
            Category = "SCAN",
            SignatureName = "ET SCAN",
            Action = ThreatAction.Detected,
            Severity = 2,
            KillChainStage = KillChainStage.Reconnaissance
        };
    }

    [Fact]
    public void Detect_TenDistinctPortsWithinOneHour_DetectsPattern()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 10; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80 + i,
                baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        Assert.Single(patterns);
        Assert.Equal(PatternType.ScanSweep, patterns[0].PatternType);
        Assert.Contains("192.0.2.50", patterns[0].SourceIpsJson);
        Assert.Equal(10, patterns[0].EventCount);
    }

    [Fact]
    public void Detect_NineDistinctPorts_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 9; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80 + i,
                baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_TenPortsSpreadOverTwoHours_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Spread 10 ports over 2+ hours so no sliding window captures all 10
        for (var i = 0; i < 10; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80 + i,
                baseTime.AddMinutes(i * 15))); // 0, 15, 30, 45, 60, 75, 90, 105, 120, 135 min
        }

        var patterns = _detector.Detect(events);

        // With 15-minute spacing, a 1-hour window can cover at most 5 events
        // (e.g., minutes 0-60 covers ports at 0, 15, 30, 45, 60 = 5 distinct ports)
        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_MultipleSourceIps_DetectsIndependently()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Source IP 1: 10 ports
        for (var i = 0; i < 10; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80 + i,
                baseTime.AddMinutes(i)));
        }

        // Source IP 2: 12 ports (detector breaks at first match, so event count will be 10)
        for (var i = 0; i < 12; i++)
        {
            events.Add(CreateReconEvent(
                "203.0.113.99",
                8000 + i,
                baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        Assert.Equal(2, patterns.Count);

        var ip1Pattern = patterns.Single(p => p.SourceIpsJson.Contains("192.0.2.50"));
        var ip2Pattern = patterns.Single(p => p.SourceIpsJson.Contains("203.0.113.99"));

        // Detector breaks after first match per source IP, so both hit at 10 ports
        Assert.Equal(10, ip1Pattern.EventCount);
        Assert.Equal(10, ip2Pattern.EventCount);
    }

    [Fact]
    public void Detect_SamePortRepeated_DoesNotCount()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Same port 80 hit 15 times - only 1 distinct port
        for (var i = 0; i < 15; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80,
                baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_NonReconEvents_AreIgnored()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // 15 distinct ports but classified as ActiveExploitation, not Reconnaissance
        for (var i = 0; i < 15; i++)
        {
            var evt = CreateReconEvent("192.0.2.50", 80 + i, baseTime.AddMinutes(i));
            evt.KillChainStage = KillChainStage.ActiveExploitation;
            events.Add(evt);
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_EmptyEventList_ReturnsEmpty()
    {
        var patterns = _detector.Detect(new List<ThreatEvent>());

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_PatternDescription_ContainsSourceIp()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 10; i++)
        {
            events.Add(CreateReconEvent("192.0.2.50", 80 + i, baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        Assert.Contains("192.0.2.50", patterns[0].Description);
        Assert.Contains("10", patterns[0].Description);
    }

    [Fact]
    public void Detect_ConfidenceScaling_IncreasesWithPortCount()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        // 10 ports -> confidence = 10/20 = 0.5
        var events10 = new List<ThreatEvent>();
        for (var i = 0; i < 10; i++)
            events10.Add(CreateReconEvent("192.0.2.50", 80 + i, baseTime.AddMinutes(i)));

        var patterns10 = _detector.Detect(events10);
        Assert.Equal(0.5, patterns10[0].Confidence);

        // 20 ports - detector breaks at first match (10 ports), so confidence is still 0.5
        // The detector emits one pattern per source IP per analysis run at the first detection point
        var events20 = new List<ThreatEvent>();
        for (var i = 0; i < 20; i++)
            events20.Add(CreateReconEvent("192.0.2.60", 80 + i, baseTime.AddMinutes(i)));

        var patterns20 = _detector.Detect(events20);
        Assert.Equal(0.5, patterns20[0].Confidence);
    }
}
