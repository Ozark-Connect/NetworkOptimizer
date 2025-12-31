using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests;

public class ConfigAuditEngineTests
{
    private readonly Mock<ILogger<ConfigAuditEngine>> _loggerMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly ConfigAuditEngine _engine;

    public ConfigAuditEngineTests()
    {
        _loggerMock = new Mock<ILogger<ConfigAuditEngine>>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();

        // Setup logger factory to return loggers for all types
        _loggerFactoryMock
            .Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        _engine = new ConfigAuditEngine(_loggerMock.Object, _loggerFactoryMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new ConfigAuditEngine(null!, _loggerFactoryMock.Object);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    [Fact]
    public void Constructor_NullLoggerFactory_ThrowsArgumentNullException()
    {
        var act = () => new ConfigAuditEngine(_loggerMock.Object, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("loggerFactory");
    }

    [Fact]
    public void Constructor_ValidParams_CreatesInstance()
    {
        var engine = new ConfigAuditEngine(_loggerMock.Object, _loggerFactoryMock.Object);

        engine.Should().NotBeNull();
    }

    #endregion

    #region RunAuditFromFile Tests

    [Fact]
    public void RunAuditFromFile_FileNotFound_ThrowsFileNotFoundException()
    {
        var act = () => _engine.RunAuditFromFile("nonexistent-file.json");

        act.Should().Throw<FileNotFoundException>()
            .WithMessage("*Device data file not found*");
    }

    [Fact]
    public void RunAuditFromFile_EmptyPath_ThrowsFileNotFoundException()
    {
        var act = () => _engine.RunAuditFromFile("   ");

        act.Should().Throw<FileNotFoundException>();
    }

    #endregion

    #region ExportToJson Tests

    [Fact]
    public void ExportToJson_ValidResult_ReturnsValidJson()
    {
        var auditResult = CreateMinimalAuditResult();

        var json = _engine.ExportToJson(auditResult);

        json.Should().NotBeNullOrEmpty();
        json.Should().StartWith("{");
        json.Should().EndWith("}");
    }

    [Fact]
    public void ExportToJson_ResultWithIssues_IncludesIssues()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Issues.Add(new AuditIssue
        {
            Type = "TEST_ISSUE",
            Message = "Test issue message",
            Severity = AuditSeverity.Critical
        });

        var json = _engine.ExportToJson(auditResult);

        json.Should().Contain("TEST_ISSUE");
        json.Should().Contain("Test issue message");
    }

    [Fact]
    public void ExportToJson_ResultWithClientName_IncludesClientName()
    {
        var auditResult = CreateMinimalAuditResult(clientName: "Test Client");

        var json = _engine.ExportToJson(auditResult);

        json.Should().Contain("Test Client");
    }

    #endregion

    #region GenerateTextReport Tests

    [Fact]
    public void GenerateTextReport_ValidResult_ReturnsNonEmptyReport()
    {
        var auditResult = CreateMinimalAuditResult();

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateTextReport_WithClientName_IncludesClientName()
    {
        var auditResult = CreateMinimalAuditResult(clientName: "Test Client Inc.");

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("Test Client Inc.");
    }

    [Fact]
    public void GenerateTextReport_WithNetworks_IncludesNetworkTopology()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Networks.Add(new NetworkInfo
        {
            Id = "net-1",
            Name = "Corporate LAN",
            VlanId = 10,
            Purpose = NetworkPurpose.Corporate,
            Subnet = "192.168.10.0/24"
        });

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("NETWORK TOPOLOGY");
        report.Should().Contain("Corporate LAN");
    }

    [Fact]
    public void GenerateTextReport_WithCriticalIssues_IncludesCriticalSection()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Issues.Add(new AuditIssue
        {
            Type = "CRITICAL_TEST",
            Message = "Critical test issue",
            Severity = AuditSeverity.Critical,
            DeviceName = "Test Switch",
            Port = "1",
            PortName = "Port 1"
        });

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("CRITICAL ISSUES");
        report.Should().Contain("Critical test issue");
    }

    [Fact]
    public void GenerateTextReport_WithRecommendedIssues_IncludesRecommendedSection()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Issues.Add(new AuditIssue
        {
            Type = "RECOMMENDED_TEST",
            Message = "Recommended improvement",
            Severity = AuditSeverity.Recommended,
            DeviceName = "Test Switch",
            Port = "2"
        });

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("RECOMMENDED IMPROVEMENTS");
        report.Should().Contain("Recommended improvement");
    }

    [Fact]
    public void GenerateTextReport_WithHardeningMeasures_IncludesMeasures()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.HardeningMeasures.Add("MAC filtering enabled on critical ports");

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("HARDENING MEASURES");
        report.Should().Contain("MAC filtering enabled");
    }

    [Fact]
    public void GenerateTextReport_WithSwitches_IncludesSwitchDetails()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Switches.Add(new SwitchInfo
        {
            Name = "Main Switch",
            Model = "USW-48-POE",
            ModelName = "Switch 48 PoE",
            IpAddress = "192.168.1.10",
            IsGateway = false,
            Capabilities = new SwitchCapabilities { MaxCustomMacAcls = 256 }
        });

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("SWITCH DETAILS");
        report.Should().Contain("Main Switch");
        report.Should().Contain("Switch 48 PoE");
    }

    [Fact]
    public void GenerateTextReport_WithGateway_MarksAsGateway()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Switches.Add(new SwitchInfo
        {
            Name = "Gateway Router",
            Model = "UDM-PRO",
            ModelName = "Dream Machine Pro",
            IsGateway = true,
            Capabilities = new SwitchCapabilities()
        });

        var report = _engine.GenerateTextReport(auditResult);

        report.Should().Contain("[Gateway]");
    }

    #endregion

    #region SaveResults Tests

    [Fact]
    public void SaveResults_InvalidFormat_ThrowsArgumentException()
    {
        var auditResult = CreateMinimalAuditResult();
        var tempPath = Path.GetTempFileName();

        try
        {
            var act = () => _engine.SaveResults(auditResult, tempPath, "invalid");

            act.Should().Throw<ArgumentException>()
                .WithMessage("*Unsupported format*");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void SaveResults_JsonFormat_WritesFile()
    {
        var auditResult = CreateMinimalAuditResult();
        var tempPath = Path.GetTempFileName();

        try
        {
            _engine.SaveResults(auditResult, tempPath, "json");

            File.Exists(tempPath).Should().BeTrue();
            var content = File.ReadAllText(tempPath);
            content.Should().StartWith("{");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void SaveResults_TextFormat_WritesFile()
    {
        var auditResult = CreateMinimalAuditResult();
        var tempPath = Path.GetTempFileName();

        try
        {
            _engine.SaveResults(auditResult, tempPath, "text");

            File.Exists(tempPath).Should().BeTrue();
            var content = File.ReadAllText(tempPath);
            content.Should().Contain("UniFi Network Security Audit Report");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void SaveResults_TxtFormat_WritesFile()
    {
        var auditResult = CreateMinimalAuditResult();
        var tempPath = Path.GetTempFileName();

        try
        {
            _engine.SaveResults(auditResult, tempPath, "txt");

            File.Exists(tempPath).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    #endregion

    #region RunAudit Basic Tests

    [Fact]
    public void RunAudit_EmptyDeviceArray_ReturnsResult()
    {
        var deviceJson = "[]";

        var result = _engine.RunAudit(deviceJson, "Test Site");

        result.Should().NotBeNull();
        result.ClientName.Should().Be("Test Site");
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void RunAudit_InvalidJson_ThrowsJsonException()
    {
        var act = () => _engine.RunAudit("not valid json");

        act.Should().Throw<System.Text.Json.JsonException>();
    }

    [Fact]
    public void RunAudit_NullClientName_SetsToNull()
    {
        var deviceJson = "[]";

        var result = _engine.RunAudit(deviceJson, clientName: null);

        result.ClientName.Should().BeNull();
    }

    [Fact]
    public void RunAudit_MinimalDevice_CalculatesScore()
    {
        var deviceJson = "[]";

        var result = _engine.RunAudit(deviceJson);

        result.SecurityScore.Should().BeGreaterThanOrEqualTo(0);
        result.SecurityScore.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void RunAudit_MinimalDevice_SetsPosture()
    {
        var deviceJson = "[]";

        var result = _engine.RunAudit(deviceJson);

        result.Posture.Should().BeDefined();
    }

    #endregion

    #region GetRecommendations Tests

    [Fact]
    public void GetRecommendations_EmptyResult_ReturnsEmptyList()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.SecurityScore = 100;

        var recommendations = _engine.GetRecommendations(auditResult);

        recommendations.Should().NotBeNull();
    }

    [Fact]
    public void GetRecommendations_WithIssues_ReturnsList()
    {
        var auditResult = CreateMinimalAuditResult();
        auditResult.Issues.Add(new AuditIssue
        {
            Type = "TEST",
            Message = "Test",
            Severity = AuditSeverity.Critical
        });

        var recommendations = _engine.GetRecommendations(auditResult);

        recommendations.Should().NotBeNull();
    }

    #endregion

    #region GenerateExecutiveSummary Tests

    [Fact]
    public void GenerateExecutiveSummary_ValidResult_ReturnsNonEmptySummary()
    {
        var auditResult = CreateMinimalAuditResult();

        var summary = _engine.GenerateExecutiveSummary(auditResult);

        summary.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Helper Methods

    private static AuditResult CreateMinimalAuditResult(string? clientName = null)
    {
        return new AuditResult
        {
            Timestamp = DateTime.UtcNow,
            ClientName = clientName,
            Networks = new List<NetworkInfo>(),
            Switches = new List<SwitchInfo>(),
            WirelessClients = new List<WirelessClientInfo>(),
            Issues = new List<AuditIssue>(),
            HardeningMeasures = new List<string>(),
            Statistics = new AuditStatistics
            {
                TotalPorts = 0,
                ActivePorts = 0,
                DisabledPorts = 0
            },
            SecurityScore = 50,
            Posture = SecurityPosture.Good
        };
    }

    #endregion
}
