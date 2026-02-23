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
    public void Detect_FiveDistinctPortsWithinWindow_DetectsPattern()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // 5 distinct ports (matches MinDistinctPorts = 5)
        for (var i = 0; i < 5; i++)
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
        Assert.Equal(5, patterns[0].EventCount);
    }

    [Fact]
    public void Detect_FourDistinctPorts_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // 4 distinct ports - below MinDistinctPorts threshold of 5
        for (var i = 0; i < 4; i++)
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
    public void Detect_FivePortsOutsideSixHourWindow_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // 5 ports spread over 8 hours (2h between each), exceeding the 6h window
        for (var i = 0; i < 5; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80 + i,
                baseTime.AddHours(i * 2))); // 0, 2, 4, 6, 8 hours
        }

        var patterns = _detector.Detect(events);

        // 6h window: at i=4 (8h), windowStart slides to i=1 (2h). Window covers 2h-8h = 4 ports.
        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_MultipleSourceIps_DetectsIndependently()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Source IP 1: 7 ports
        for (var i = 0; i < 7; i++)
        {
            events.Add(CreateReconEvent(
                "192.0.2.50",
                80 + i,
                baseTime.AddMinutes(i)));
        }

        // Source IP 2: 8 ports
        for (var i = 0; i < 8; i++)
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

        // Detector breaks after first match per source IP (at 5 distinct ports)
        Assert.Equal(5, ip1Pattern.EventCount);
        Assert.Equal(5, ip2Pattern.EventCount);
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
    public void Detect_PatternDescription_ContainsSourceIpAndPortCount()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 5; i++)
        {
            events.Add(CreateReconEvent("192.0.2.50", 80 + i, baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        Assert.Contains("192.0.2.50", patterns[0].Description);
        Assert.Contains("5", patterns[0].Description);
    }

    [Fact]
    public void Detect_ConfidenceScaling_IncreasesWithPortCount()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        // 5 ports -> confidence = 5/15 = 0.333
        var events5 = new List<ThreatEvent>();
        for (var i = 0; i < 5; i++)
            events5.Add(CreateReconEvent("192.0.2.50", 80 + i, baseTime.AddMinutes(i)));

        var patterns5 = _detector.Detect(events5);
        Assert.Equal(5.0 / 15.0, patterns5[0].Confidence);

        // 15+ ports -> confidence capped at 1.0
        var events15 = new List<ThreatEvent>();
        for (var i = 0; i < 15; i++)
            events15.Add(CreateReconEvent("192.0.2.60", 80 + i, baseTime.AddMinutes(i)));

        var patterns15 = _detector.Detect(events15);
        // Detector breaks at first match (5 distinct ports), confidence = 5/15
        Assert.Equal(5.0 / 15.0, patterns15[0].Confidence);
    }

    [Fact]
    public void Detect_MonitoredEvents_AreIncluded()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Monitored events (severity 1) should still be included in scan detection
        for (var i = 0; i < 5; i++)
        {
            var evt = CreateReconEvent("192.0.2.50", 80 + i, baseTime.AddMinutes(i));
            evt.KillChainStage = KillChainStage.Monitored;
            events.Add(evt);
        }

        var patterns = _detector.Detect(events);

        Assert.Single(patterns);
    }
}
