using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class DDoSDetectorTests
{
    private readonly DDoSDetector _detector = new();

    [Fact]
    public void Detect_SufficientEventsAndSources_DetectsPattern()
    {
        var events = new List<ThreatEvent>();
        var now = DateTime.UtcNow;

        // 100 events from 10 unique sources within 5 minutes
        for (var i = 0; i < 100; i++)
        {
            events.Add(new ThreatEvent
            {
                SourceIp = $"198.51.100.{(i % 10) + 1}",
                DestIp = "192.0.2.1",
                DestPort = 80,
                SignatureId = 1000 + i,
                SignatureName = "DDoS packet",
                Timestamp = now.AddSeconds(-i * 2), // 2 seconds apart = 200s total < 5min
                Action = ThreatAction.Blocked
            });
        }

        var patterns = _detector.Detect(events);

        Assert.Single(patterns);
        Assert.Equal(PatternType.DDoS, patterns[0].PatternType);
        Assert.Equal(80, patterns[0].TargetPort);
    }

    [Fact]
    public void Detect_NotEnoughEvents_ReturnsEmpty()
    {
        var events = new List<ThreatEvent>();
        var now = DateTime.UtcNow;

        // Only 50 events (need 100)
        for (var i = 0; i < 50; i++)
        {
            events.Add(new ThreatEvent
            {
                SourceIp = $"198.51.100.{(i % 10) + 1}",
                DestIp = "192.0.2.1",
                DestPort = 80,
                SignatureId = 1000,
                SignatureName = "DDoS packet",
                Timestamp = now.AddSeconds(-i),
                Action = ThreatAction.Blocked
            });
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_NotEnoughUniqueSources_ReturnsEmpty()
    {
        var events = new List<ThreatEvent>();
        var now = DateTime.UtcNow;

        // 100 events but only from 5 sources (need 10)
        for (var i = 0; i < 100; i++)
        {
            events.Add(new ThreatEvent
            {
                SourceIp = $"198.51.100.{(i % 5) + 1}",
                DestIp = "192.0.2.1",
                DestPort = 80,
                SignatureId = 1000,
                SignatureName = "Flood packet",
                Timestamp = now.AddSeconds(-i * 2),
                Action = ThreatAction.Blocked
            });
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_EmptyEvents_ReturnsEmpty()
    {
        var patterns = _detector.Detect([]);
        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_EventsSpreadTooFarApart_ReturnsEmpty()
    {
        var events = new List<ThreatEvent>();
        var now = DateTime.UtcNow;

        // 100 events from 10 sources but spread over 10 minutes (window is 5 min)
        for (var i = 0; i < 100; i++)
        {
            events.Add(new ThreatEvent
            {
                SourceIp = $"198.51.100.{(i % 10) + 1}",
                DestIp = "192.0.2.1",
                DestPort = 80,
                SignatureId = 1000,
                SignatureName = "DDoS packet",
                Timestamp = now.AddSeconds(-i * 6), // 6 seconds apart = 600s total = 10min
                Action = ThreatAction.Blocked
            });
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }
}
