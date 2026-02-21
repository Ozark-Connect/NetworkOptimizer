using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class ExposureValidatorTests
{
    private readonly ExposureValidator _validator;
    private readonly Mock<IThreatRepository> _mockRepo;
    private readonly DateTime _from = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly DateTime _to = new(2025, 1, 31, 23, 59, 59, DateTimeKind.Utc);

    public ExposureValidatorTests()
    {
        var logger = new Mock<ILogger<ExposureValidator>>();
        _mockRepo = new Mock<IThreatRepository>();
        _validator = new ExposureValidator(logger.Object);
    }

    private static UniFiPortForwardRule CreatePortForwardRule(
        string dstPort,
        string? fwd = "198.51.100.10",
        string? fwdPort = null,
        string? name = null,
        string? proto = "tcp")
    {
        return new UniFiPortForwardRule
        {
            DstPort = dstPort,
            Fwd = fwd,
            FwdPort = fwdPort,
            Name = name,
            Proto = proto
        };
    }

    private static ThreatEvent CreateThreatEvent(
        string sourceIp,
        int destPort,
        string signatureName = "ET Test Signature",
        int severity = 3)
    {
        return new ThreatEvent
        {
            InnerAlertId = Guid.NewGuid().ToString(),
            Timestamp = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            SourceIp = sourceIp,
            DestIp = "198.51.100.1",
            DestPort = destPort,
            Protocol = "TCP",
            Category = "Misc",
            SignatureName = signatureName,
            Action = ThreatAction.Blocked,
            Severity = severity
        };
    }

    [Fact]
    public async Task ValidateAsync_PortForwardMatchingThreatDestPort_GeneratesExposedService()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("443", name: "Web Server")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { { 443, 15 } });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, 443, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>
            {
                CreateThreatEvent("192.0.2.10", 443, "ET EXPLOIT TLS vuln"),
                CreateThreatEvent("192.0.2.11", 443, "ET EXPLOIT TLS vuln"),
                CreateThreatEvent("203.0.113.5", 443, "ET SCAN port probe")
            });

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        Assert.Single(report.ExposedServices);
        var svc = report.ExposedServices[0];
        Assert.Equal(443, svc.Port);
        Assert.Equal("Web Server", svc.ServiceName);
        Assert.Equal(15, svc.ThreatCount);
        Assert.Equal(3, svc.UniqueSourceIps);
        Assert.Equal(15, report.TotalThreatsTargetingExposed);
    }

    [Fact]
    public async Task ValidateAsync_PortForwardNotTargeted_ReturnsZeroThreats()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("8080", name: "Dev Server")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int>()); // No threats on any port

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        Assert.Empty(report.ExposedServices);
        Assert.Equal(0, report.TotalThreatsTargetingExposed);
        Assert.Equal(0, report.TotalExposedPorts);
    }

    [Fact]
    public async Task ValidateAsync_GeoBlockRecommendation_GeneratedWithCountryData()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("22", name: "SSH")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { { 22, 50 } });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, 22, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>
            {
                CreateThreatEvent("192.0.2.10", 22),
                CreateThreatEvent("192.0.2.11", 22)
            });

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>
            {
                { "CN", 30 },
                { "RU", 15 },
                { "US", 3 },
                { "DE", 2 }
            });

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        Assert.NotNull(report.GeoBlockRecommendation);
        Assert.Contains("CN", report.GeoBlockRecommendation!.Countries);
        Assert.Contains("RU", report.GeoBlockRecommendation.Countries);
        Assert.True(report.GeoBlockRecommendation.PreventionPercentage > 0);
    }

    [Fact]
    public async Task ValidateAsync_EmptyRules_ReturnsEmptyReport()
    {
        var report = await _validator.ValidateAsync(
            new List<UniFiPortForwardRule>(),
            _mockRepo.Object,
            _from,
            _to);

        Assert.Empty(report.ExposedServices);
        Assert.Equal(0, report.TotalExposedPorts);
        Assert.Equal(0, report.TotalThreatsTargetingExposed);
        Assert.Null(report.GeoBlockRecommendation);
    }

    [Fact]
    public async Task ValidateAsync_NullRules_ReturnsEmptyReport()
    {
        var report = await _validator.ValidateAsync(
            null,
            _mockRepo.Object,
            _from,
            _to);

        Assert.Empty(report.ExposedServices);
        Assert.Equal(0, report.TotalExposedPorts);
    }

    [Fact]
    public async Task ValidateAsync_MultipleRulesMultiplePorts_TracksEachService()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("22", name: "SSH"),
            CreatePortForwardRule("443", name: "Web Server"),
            CreatePortForwardRule("3389", name: "RDP")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int>
            {
                { 22, 100 },
                { 443, 50 },
                { 3389, 25 }
            });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, It.IsAny<int>(), null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>
            {
                CreateThreatEvent("192.0.2.10", 22)
            });

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        Assert.Equal(3, report.ExposedServices.Count);
        Assert.Equal(3, report.TotalExposedPorts);
        Assert.Equal(175, report.TotalThreatsTargetingExposed); // 100 + 50 + 25
    }

    [Fact]
    public async Task ValidateAsync_PortRange_ExpandsAndMatches()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("8080-8082", name: "Dev Ports")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int>
            {
                { 8080, 10 },
                { 8081, 5 }
                // 8082 has no threats
            });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, It.IsAny<int>(), null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>
            {
                CreateThreatEvent("192.0.2.10", 8080)
            });

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        // Only ports with threats are included as exposed services
        Assert.Equal(2, report.ExposedServices.Count);
        Assert.Equal(15, report.TotalThreatsTargetingExposed);
    }

    [Fact]
    public async Task ValidateAsync_TopSignatures_LimitedToFive()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("22", name: "SSH")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { { 22, 100 } });

        var events = new List<ThreatEvent>();
        for (var i = 0; i < 7; i++)
        {
            for (var j = 0; j < 10 - i; j++)
            {
                events.Add(CreateThreatEvent($"192.0.2.{10 + i}", 22, $"Signature {i}"));
            }
        }

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, 22, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        Assert.Single(report.ExposedServices);
        Assert.True(report.ExposedServices[0].TopSignatures.Count <= 5);
    }

    [Fact]
    public async Task ValidateAsync_GeoBlockRecommendation_NotGeneratedWithFewThreats()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("22", name: "SSH")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { { 22, 5 } });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, 22, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>());

        // Less than 10 total threats in country distribution
        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>
            {
                { "CN", 5 },
                { "US", 3 }
            });

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        // Total < 10, so no recommendation
        Assert.Null(report.GeoBlockRecommendation);
    }

    [Fact]
    public async Task ValidateAsync_ForwardTarget_FormattedCorrectly()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("443", fwd: "198.51.100.20", fwdPort: "8443", name: "Internal Server")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { { 443, 10 } });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, 443, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>());

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        Assert.Equal("198.51.100.20:8443", report.ExposedServices[0].ForwardTarget);
    }

    [Fact]
    public async Task ValidateAsync_SeverityBreakdown_PopulatedFromEvents()
    {
        var rules = new List<UniFiPortForwardRule>
        {
            CreatePortForwardRule("22", name: "SSH")
        };

        _mockRepo.Setup(r => r.GetThreatCountsByPortAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<int, int> { { 22, 30 } });

        _mockRepo.Setup(r => r.GetEventsAsync(_from, _to, null, 22, null, 500, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ThreatEvent>
            {
                CreateThreatEvent("192.0.2.10", 22, severity: 5),
                CreateThreatEvent("192.0.2.11", 22, severity: 5),
                CreateThreatEvent("192.0.2.12", 22, severity: 3),
                CreateThreatEvent("192.0.2.13", 22, severity: 2)
            });

        _mockRepo.Setup(r => r.GetCountryDistributionAsync(_from, _to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var report = await _validator.ValidateAsync(rules, _mockRepo.Object, _from, _to);

        var breakdown = report.ExposedServices[0].SeverityBreakdown;
        Assert.Equal(2, breakdown[5]);
        Assert.Equal(1, breakdown[3]);
        Assert.Equal(1, breakdown[2]);
    }
}
