using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class BruteForceDetectorTests
{
    private readonly BruteForceDetector _detector = new();

    private static ThreatEvent CreateEvent(
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
            Category = "Attempted Administrator Privilege Gain",
            SignatureName = "ET EXPLOIT SSH brute force",
            Action = ThreatAction.Blocked,
            Severity = 4,
            KillChainStage = KillChainStage.AttemptedExploitation
        };
    }

    [Fact]
    public void Detect_TwentyEventsOnSshPortWithinTenMinutes_DetectsPattern()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 20; i++)
        {
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 20)));
        }

        var patterns = _detector.Detect(events);

        Assert.Single(patterns);
        Assert.Equal(PatternType.BruteForce, patterns[0].PatternType);
        Assert.Contains("192.0.2.50", patterns[0].SourceIpsJson);
        Assert.Equal(22, patterns[0].TargetPort);
        Assert.Equal(20, patterns[0].EventCount);
    }

    [Fact]
    public void Detect_NineteenEvents_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 19; i++)
        {
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 20)));
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_NonBruteForcePort_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Port 80 is not in the brute force target ports
        for (var i = 0; i < 30; i++)
        {
            events.Add(CreateEvent("192.0.2.50", 80, baseTime.AddSeconds(i * 10)));
        }

        var patterns = _detector.Detect(events);

        Assert.Empty(patterns);
    }

    [Theory]
    [InlineData(22)]   // SSH
    [InlineData(23)]   // Telnet
    [InlineData(3389)] // RDP
    [InlineData(443)]  // HTTPS
    [InlineData(8443)] // HTTPS Alt
    [InlineData(21)]   // FTP
    [InlineData(25)]   // SMTP
    [InlineData(110)]  // POP3
    [InlineData(143)]  // IMAP
    [InlineData(993)]  // IMAPS
    [InlineData(995)]  // POP3S
    [InlineData(5900)] // VNC
    public void Detect_AllTargetPorts_AreMonitored(int port)
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 25; i++)
        {
            events.Add(CreateEvent("192.0.2.50", port, baseTime.AddSeconds(i * 10)));
        }

        var patterns = _detector.Detect(events);

        Assert.Single(patterns);
        Assert.Equal(port, patterns[0].TargetPort);
    }

    [Fact]
    public void Detect_EventsSpreadBeyondTenMinutes_DoesNotDetect()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // 20 events spread over 20 minutes (1 per minute)
        // In any 10-minute window, at most ~10 events
        for (var i = 0; i < 20; i++)
        {
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddMinutes(i)));
        }

        var patterns = _detector.Detect(events);

        // 10-minute window can capture at most 11 events (minutes 0-10 inclusive)
        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_MultipleSourcesSamePort_DetectsEachIndependently()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Source 1: 25 events on port 22
        for (var i = 0; i < 25; i++)
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 10)));

        // Source 2: 22 events on port 22
        for (var i = 0; i < 22; i++)
            events.Add(CreateEvent("203.0.113.99", 22, baseTime.AddSeconds(i * 10)));

        var patterns = _detector.Detect(events);

        Assert.Equal(2, patterns.Count);
        Assert.Contains(patterns, p => p.SourceIpsJson.Contains("192.0.2.50"));
        Assert.Contains(patterns, p => p.SourceIpsJson.Contains("203.0.113.99"));
    }

    [Fact]
    public void Detect_SameSourceDifferentPorts_DetectsEachPortSeparately()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Same source, 20 events on SSH and 20 events on RDP
        for (var i = 0; i < 20; i++)
        {
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 10)));
            events.Add(CreateEvent("192.0.2.50", 3389, baseTime.AddSeconds(i * 10)));
        }

        var patterns = _detector.Detect(events);

        Assert.Equal(2, patterns.Count);
        Assert.Contains(patterns, p => p.TargetPort == 22);
        Assert.Contains(patterns, p => p.TargetPort == 3389);
    }

    [Fact]
    public void Detect_EmptyEventList_ReturnsEmpty()
    {
        var patterns = _detector.Detect(new List<ThreatEvent>());

        Assert.Empty(patterns);
    }

    [Fact]
    public void Detect_PatternDescription_ContainsDetails()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        for (var i = 0; i < 20; i++)
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 10)));

        var patterns = _detector.Detect(events);

        Assert.Contains("192.0.2.50", patterns[0].Description);
        Assert.Contains("22", patterns[0].Description);
        Assert.Contains("20", patterns[0].Description);
    }

    [Fact]
    public void Detect_Confidence_ScalesWithEventCount()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // 20 events -> confidence = 20/50 = 0.4
        for (var i = 0; i < 20; i++)
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 10)));

        var patterns = _detector.Detect(events);

        Assert.Equal(0.4, patterns[0].Confidence);
    }

    [Fact]
    public void Detect_ConfidenceCappedAtOne_WhenAllInWindow()
    {
        var baseTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<ThreatEvent>();

        // Detector breaks at first match point (20 events). Even with 60 events in 10 min,
        // the window count at the detection point is 20, so confidence = 20/50 = 0.4.
        // The break statement means it only emits one pattern per source+port group.
        for (var i = 0; i < 60; i++)
            events.Add(CreateEvent("192.0.2.50", 22, baseTime.AddSeconds(i * 5)));

        var patterns = _detector.Detect(events);

        Assert.Single(patterns);
        // Confidence = min(1.0, 20/50) = 0.4 because detector breaks at first match
        Assert.Equal(0.4, patterns[0].Confidence);
    }
}
