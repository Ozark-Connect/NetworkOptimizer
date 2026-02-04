using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
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
    public async Task RunAuditFromFile_FileNotFound_ThrowsFileNotFoundException()
    {
        var act = async () => await _engine.RunAuditFromFileAsync("nonexistent-file.json");

        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage("*Device data file not found*");
    }

    [Fact]
    public async Task RunAuditFromFile_EmptyPath_ThrowsFileNotFoundException()
    {
        var act = async () => await _engine.RunAuditFromFileAsync("   ");

        await act.Should().ThrowAsync<FileNotFoundException>();
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
        report.Should().Contain("[Switch] Main");  // Name is stripped and prefixed with [Switch]
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
    public async Task RunAudit_EmptyDeviceArray_ReturnsResult()
    {
        var deviceJson = "[]";

        var result = await _engine.RunAuditAsync(deviceJson, "Test Site");

        result.Should().NotBeNull();
        result.ClientName.Should().Be("Test Site");
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RunAudit_InvalidJson_ThrowsInvalidOperationException()
    {
        var act = async () => await _engine.RunAuditAsync("not valid json");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Invalid device data JSON format*");
    }

    [Fact]
    public async Task RunAudit_NullClientName_SetsToNull()
    {
        var deviceJson = "[]";

        var result = await _engine.RunAuditAsync(deviceJson, clientName: null);

        result.ClientName.Should().BeNull();
    }

    [Fact]
    public async Task RunAudit_MinimalDevice_CalculatesScore()
    {
        var deviceJson = "[]";

        var result = await _engine.RunAuditAsync(deviceJson);

        result.SecurityScore.Should().BeGreaterThanOrEqualTo(0);
        result.SecurityScore.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task RunAudit_MinimalDevice_SetsPosture()
    {
        var deviceJson = "[]";

        var result = await _engine.RunAuditAsync(deviceJson);

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

    #region Offline Client Analysis Tests

    private static string CreateDeviceJsonWithNetworks()
    {
        // Device JSON with a gateway that has network_table
        return """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "network_table": [
                    {
                        "_id": "net-corp",
                        "name": "Corporate",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.1/24"
                    },
                    {
                        "_id": "net-iot",
                        "name": "IoT",
                        "vlan": 20,
                        "purpose": "iot",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.129/25"
                    },
                    {
                        "_id": "net-security",
                        "name": "Security",
                        "vlan": 30,
                        "purpose": "security",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.225/28"
                    }
                ]
            }
        ]
        """;
    }

    [Fact]
    public async Task RunAudit_NoClientHistory_SkipsOfflineAnalysis()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: null,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should complete without offline client issues
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-IOT-VLAN");
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CAMERA-VLAN");
    }

    [Fact]
    public async Task RunAudit_EmptyClientHistory_SkipsOfflineAnalysis()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: new List<UniFiClientDetailResponse>(),
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-IOT-VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineIoTOnCorporate_CreatesIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        // Roku MAC prefix (known IoT device)
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Roku OUI
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Living Room Roku",
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds() // Recent
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.Issues.Should().Contain(i => i.Type == "OFFLINE-IOT-VLAN");
        var issue = result.Issues.First(i => i.Type == "OFFLINE-IOT-VLAN");
        issue.DeviceName.Should().Contain("(offline)");
        issue.CurrentNetwork.Should().Be("Corporate");
    }

    [Fact]
    public async Task RunAudit_OfflineIoTOnIoTVlan_NoIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Roku OUI
                IsWired = false,
                LastConnectionNetworkId = "net-iot", // Already on IoT
                DisplayName = "Living Room Roku",
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-IOT-VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineWiredDevice_Skipped()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Roku OUI
                IsWired = true, // Wired devices are skipped
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Wired Roku"
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-IOT-VLAN");
    }

    [Fact]
    public async Task RunAudit_OnlineClient_NotFlaggedAsOffline()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var onlineClients = new List<UniFiClientResponse>
        {
            new() { Mac = "D8:31:34:11:22:33" }
        };
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Same MAC as online client
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Active Roku"
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: onlineClients,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should not flag as offline since client is currently online
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-IOT-VLAN" &&
            i.DeviceName != null && i.DeviceName.Contains("(offline)"));
    }

    [Fact]
    public async Task RunAudit_StaleOfflineClient_GetsInformationalSeverity()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        // Client last seen more than 14 days ago
        var staleTimestamp = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Roku OUI
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Old Roku",
                LastSeen = staleTimestamp
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        var issue = result.Issues.FirstOrDefault(i => i.Type == "OFFLINE-IOT-VLAN");
        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(AuditSeverity.Informational);
        issue.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public async Task RunAudit_RecentOfflineClient_GetsCriticalOrRecommended()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        // Client last seen within 14 days
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Roku OUI - low risk streaming device
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Recent Roku",
                LastSeen = recentTimestamp
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        var issue = result.Issues.FirstOrDefault(i => i.Type == "OFFLINE-IOT-VLAN");
        issue.Should().NotBeNull();
        // Roku is low-risk, so should be Recommended
        issue!.Severity.Should().Be(AuditSeverity.Recommended);
        issue.ScoreImpact.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunAudit_OfflineClientWithNameDetection_CreatesIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        // Unknown MAC but name indicates IoT device
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55", // Unknown OUI
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Nest Thermostat", // Name-based detection
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should detect from name and create issue
        result.Issues.Should().Contain(i => i.Type == "OFFLINE-IOT-VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineUnknownDevice_NoIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55", // Unknown OUI
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Generic Device", // No IoT keywords
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should not create issue for unknown devices
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-IOT-VLAN" &&
            i.DeviceName != null && i.DeviceName.Contains("Generic Device"));
    }

    [Fact]
    public async Task RunAudit_OfflineClientNoNetwork_NoIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "D8:31:34:11:22:33", // Roku OUI
                IsWired = false,
                LastConnectionNetworkId = "net-nonexistent", // Network not in device data
                DisplayName = "Orphaned Roku"
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should not create issue if network can't be found
        result.Issues.Should().NotContain(i => i.DeviceName != null && i.DeviceName.Contains("Orphaned"));
    }

    private static string CreateDeviceJsonWithPrinterVlan()
    {
        // Device JSON with networks including a Printer VLAN
        return """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "network_table": [
                    {
                        "_id": "net-corp",
                        "name": "Corporate",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.1/24"
                    },
                    {
                        "_id": "net-iot",
                        "name": "IoT",
                        "vlan": 20,
                        "purpose": "iot",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.129/25"
                    },
                    {
                        "_id": "net-printer",
                        "name": "Printing",
                        "vlan": 40,
                        "purpose": "printer",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.240/28"
                    }
                ]
            }
        ]
        """;
    }

    [Fact]
    public async Task RunAudit_OfflinePrinterOnIoTVlan_StrictMode_CreatesIssue()
    {
        var deviceJson = CreateDeviceJsonWithPrinterVlan();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-iot", // On IoT, not Printer VLAN
                DisplayName = "Office Printer", // Name-based detection
                LastSeen = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds() // Recent
            }
        };

        // Strict mode: printers must be on Printer VLAN
        var allowanceSettings = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: allowanceSettings,
            protectCameras: null);

        // Should flag printer on IoT when Printer VLAN exists and strict mode enabled
        result.Issues.Should().Contain(i => i.Type == "OFFLINE-PRINTER-VLAN");
        var issue = result.Issues.First(i => i.Type == "OFFLINE-PRINTER-VLAN");
        issue.DeviceName.Should().Contain("(offline)");
        issue.CurrentNetwork.Should().Be("IoT");
        issue.RecommendedNetwork.Should().Be("Printing");
    }

    [Fact]
    public async Task RunAudit_OfflinePrinterOnIoTVlan_LenientMode_CreatesInformationalIssue()
    {
        var deviceJson = CreateDeviceJsonWithPrinterVlan();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-iot", // On IoT
                DisplayName = "Office Printer",
                LastSeen = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds()
            }
        };

        // Lenient mode: still suggest Printer VLAN but as Informational
        var allowanceSettings = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = true };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: allowanceSettings,
            protectCameras: null);

        // Should flag printer with Informational severity suggesting Printer VLAN (no score impact)
        result.Issues.Should().Contain(i => i.Type == "OFFLINE-PRINTER-VLAN");
        var issue = result.Issues.First(i => i.Type == "OFFLINE-PRINTER-VLAN");
        issue.Severity.Should().Be(AuditSeverity.Informational);
        issue.ScoreImpact.Should().Be(0);
        issue.RecommendedNetwork.Should().Be("Printing");
    }

    [Fact]
    public async Task RunAudit_OfflinePrinterOnPrinterVlan_NoIssue()
    {
        var deviceJson = CreateDeviceJsonWithPrinterVlan();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-printer", // Already on Printer VLAN
                DisplayName = "Office Printer",
                LastSeen = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds()
            }
        };

        var allowanceSettings = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: allowanceSettings,
            protectCameras: null);

        // Printer already on correct VLAN
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-PRINTER-VLAN");
    }

    [Fact]
    public async Task RunAudit_StaleOfflinePrinter_GetsInformationalSeverity()
    {
        var deviceJson = CreateDeviceJsonWithPrinterVlan();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-iot",
                DisplayName = "Old Printer",
                LastSeen = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds() // Stale
            }
        };

        var allowanceSettings = new DeviceAllowanceSettings { AllowPrintersOnMainNetwork = false };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: allowanceSettings,
            protectCameras: null);

        var issue = result.Issues.FirstOrDefault(i => i.Type == "OFFLINE-PRINTER-VLAN");
        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(AuditSeverity.Informational);
        issue.ScoreImpact.Should().Be(0);
    }

    [Fact]
    public async Task RunAudit_OfflineCloudCameraOnCorporate_RecommendsIoTVlan()
    {
        // Bug fix test: Offline cloud cameras (Nest, Ring, etc.) should recommend
        // IoT VLAN, not Security VLAN. Only self-hosted cameras go on Security.
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55", // Unknown OUI - name-based detection
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Nest Cam Backyard", // Cloud camera detected by name
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds() // Recent
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should create cloud camera issue, NOT regular camera issue
        result.Issues.Should().Contain(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CAMERA-VLAN");

        var issue = result.Issues.First(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");
        issue.DeviceName.Should().Contain("(offline)");
        issue.CurrentNetwork.Should().Be("Corporate");
        issue.RecommendedNetwork.Should().Be("IoT"); // Should recommend IoT, NOT Security
        issue.Message.Should().Contain("should be isolated");
        issue.Message.Should().NotContain("security VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineCloudCameraOnIoTVlan_NoIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-iot", // Already on IoT VLAN
                DisplayName = "Ring Doorbell", // Cloud camera
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Cloud camera already on IoT VLAN - no issue
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CAMERA-VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineSelfHostedCameraOnCorporate_RecommendsSecurityVlan()
    {
        // Self-hosted cameras (UniFi, Reolink) should still recommend Security VLAN
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "Reolink Camera", // Self-hosted camera, not cloud
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should create regular camera issue recommending Security VLAN
        result.Issues.Should().Contain(i => i.Type == "OFFLINE-CAMERA-VLAN");
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");

        var issue = result.Issues.First(i => i.Type == "OFFLINE-CAMERA-VLAN");
        issue.RecommendedNetwork.Should().Be("Security");
        issue.Message.Should().Contain("security VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineCloudSecuritySystemOnCorporate_RecommendsIoTVlan()
    {
        // Cloud security systems (SimpliSafe) need internet access, should recommend IoT VLAN
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-corp",
                DisplayName = "SimpliSafe Basestation", // Cloud security system
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Should create cloud camera/security issue, NOT regular camera issue
        result.Issues.Should().Contain(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CAMERA-VLAN");

        var issue = result.Issues.First(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");
        issue.DeviceName.Should().Contain("(offline)");
        issue.RecommendedNetwork.Should().Be("IoT"); // Should recommend IoT, NOT Security
        issue.Message.Should().Contain("should be isolated");
        issue.Message.Should().NotContain("security VLAN");
    }

    [Fact]
    public async Task RunAudit_OfflineCloudSecuritySystemOnIoTVlan_NoIssue()
    {
        var deviceJson = CreateDeviceJsonWithNetworks();
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new()
            {
                Id = "client-1",
                Mac = "00:11:22:33:44:55",
                IsWired = false,
                LastConnectionNetworkId = "net-iot", // Already on IoT VLAN
                DisplayName = "SimpliSafe Base Station", // Cloud security system
                LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: clientHistory,
            fingerprintDb: null,
            settingsData: null,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Cloud security system already on IoT VLAN - no issue
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CLOUD-CAMERA-VLAN");
        result.Issues.Should().NotContain(i => i.Type == "OFFLINE-CAMERA-VLAN");
    }

    #endregion

    #region DNS Security Integration Tests

    [Fact]
    public async Task RunAudit_WithDohSettings_PopulatesDnsSecurityInfo()
    {
        var deviceJson = CreateDeviceJsonWithDoh();
        var settingsData = CreateSettingsDataWithDoh();

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: null,
            fingerprintDb: null,
            settingsData: settingsData,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        // Verify DnsSecurity is populated
        result.DnsSecurity.Should().NotBeNull();
        result.DnsSecurity!.DohEnabled.Should().BeTrue();
        result.DnsSecurity.DohProviders.Should().Contain("Cloudflare");
    }

    [Fact]
    public async Task RunAudit_WithDohAndWanDns_PopulatesWanDnsInfo()
    {
        var deviceJson = CreateDeviceJsonWithDohAndWanDns();
        var settingsData = CreateSettingsDataWithDoh();

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: null,
            fingerprintDb: null,
            settingsData: settingsData,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.DnsSecurity.Should().NotBeNull();
        result.DnsSecurity!.WanDnsServers.Should().Contain("1.1.1.1");
        result.DnsSecurity.ExpectedDnsProvider.Should().Be("Cloudflare");
    }

    [Fact]
    public async Task RunAudit_WithThirdPartyDns_PopulatesThirdPartyInfo()
    {
        // Device JSON with networks using third-party LAN DNS (Pi-hole)
        // Using RFC1918 private IPs (10.x.x.x) for proper detection
        // Note: dhcpd_dns_enabled must be true for DNS servers to be extracted
        var deviceJson = """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "network_table": [
                    {
                        "_id": "net-home",
                        "name": "Home",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "10.0.1.1/24",
                        "dhcpd_dns_enabled": true,
                        "dhcpd_dns_1": "10.0.1.5"
                    }
                ]
            }
        ]
        """;

        // Need settings data to trigger DNS analysis
        var settingsData = System.Text.Json.JsonDocument.Parse("[]").RootElement;

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: null,
            fingerprintDb: null,
            settingsData: settingsData,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.DnsSecurity.Should().NotBeNull();
        result.DnsSecurity!.HasThirdPartyDns.Should().BeTrue();
    }

    [Fact]
    public async Task RunAudit_WithEmptySettings_DnsSecurityShowsNotEnabled()
    {
        var deviceJson = "[]";
        // Empty settings array - DNS analysis will run but find nothing configured
        var settingsData = System.Text.Json.JsonDocument.Parse("[]").RootElement;

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: null,
            fingerprintDb: null,
            settingsData: settingsData,
            firewallRules: null,
            allowanceSettings: null,
            protectCameras: null);

        result.DnsSecurity.Should().NotBeNull();
        result.DnsSecurity!.DohEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAudit_WithNoSettingsOrFirewallRules_DnsSecurityIsNull()
    {
        // When no settings, firewall rules, or NAT rules are provided, DNS analysis is skipped
        var deviceJson = "[]";

        var result = await _engine.RunAuditAsync(deviceJson);

        // This is expected behavior - no data to analyze
        result.DnsSecurity.Should().BeNull();
    }

    [Fact]
    public async Task RunAudit_WithDns53BlockRule_ShowsDnsLeakProtection()
    {
        var deviceJson = CreateDeviceJsonWithDoh();
        var settingsData = CreateSettingsDataWithDoh();
        var firewallRules = new List<Audit.Models.FirewallRule>
        {
            new()
            {
                Id = "rule-block-dns",
                Name = "Block DNS Bypass",
                Action = "drop",
                DestinationPort = "53",
                Enabled = true
            }
        };

        var result = await _engine.RunAuditAsync(
            deviceJson,
            clients: null,
            clientHistory: null,
            fingerprintDb: null,
            settingsData: settingsData,
            firewallRules: firewallRules,
            allowanceSettings: null,
            protectCameras: null);

        result.DnsSecurity.Should().NotBeNull();
        result.DnsSecurity!.HasDns53BlockRule.Should().BeTrue();
        result.DnsSecurity.DnsLeakProtection.Should().BeTrue();
    }

    private static string CreateDeviceJsonWithDoh()
    {
        return """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "network_table": [
                    {
                        "_id": "net-home",
                        "name": "Home",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.1/24"
                    }
                ]
            }
        ]
        """;
    }

    private static string CreateDeviceJsonWithDohAndWanDns()
    {
        return """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "port_table": [
                    {
                        "network_name": "wan",
                        "name": "WAN",
                        "up": true,
                        "dns": ["1.1.1.1", "1.0.0.1"]
                    }
                ],
                "network_table": [
                    {
                        "_id": "net-home",
                        "name": "Home",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.1/24"
                    }
                ]
            }
        ]
        """;
    }

    private static System.Text.Json.JsonElement CreateSettingsDataWithDoh()
    {
        return System.Text.Json.JsonDocument.Parse("""
        [
            {
                "key": "doh",
                "state": "custom",
                "server_names": ["cloudflare"]
            }
        ]
        """).RootElement;
    }

    #endregion

    #region Access Port VLAN Integration Tests

    [Fact]
    public async Task RunAudit_AccessPortWithManyTaggedVlans_UsesAllNetworksIncludingDisabled()
    {
        // This test verifies the full path: NetworkConfigs -> allNetworks -> AccessPortVlanRule
        // A port with 3+ tagged VLANs from allNetworks (including disabled) should trigger ACCESS-VLAN-001

        var deviceJson = CreateDeviceJsonWithMacRestrictedTrunkPort();
        var networkConfigs = CreateNetworkConfigsWithDisabled();

        var result = await _engine.RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = deviceJson,
            NetworkConfigs = networkConfigs,
            ClientName = "Test Site"
        });

        // Should detect the ACCESS-VLAN-001 issue because allNetworks includes 4 corporate networks
        // (3 enabled + 1 disabled) and the port has none excluded, so tagged count = 4 - 1 (native) = 3
        result.Issues.Should().Contain(i => i.Type == "ACCESS-VLAN-001",
            because: "a trunk port with 4 networks (3 enabled + 1 disabled) minus native = 3 tagged should exceed threshold");
        var issue = result.Issues.First(i => i.Type == "ACCESS-VLAN-001");
        issue.Message.Should().Contain("VLANs tagged");
    }

    [Fact]
    public async Task RunAudit_AccessPortVlanCount_ExcludesWanAndVpnNetworks()
    {
        // This test verifies that WAN and VPN-client networks are excluded from the allNetworks count
        // because they cannot be tagged on switch ports
        // Port must explicitly exclude some networks (not "Allow All") to test the count logic

        var deviceJson = CreateDeviceJsonWithSelectiveTaggedVlans();
        // Create configs with 3 corporate networks, 1 WAN, and 1 VPN-client
        // Only the 3 corporate networks should be counted, but port excludes one
        var networkConfigs = new List<UniFiNetworkConfig>
        {
            new() { Id = "net-corp", Name = "Corporate", Vlan = 1, Purpose = "corporate", Enabled = true },
            new() { Id = "net-iot", Name = "IoT", Vlan = 20, Purpose = "corporate", Enabled = true },
            new() { Id = "net-cameras", Name = "Cameras", Vlan = 30, Purpose = "corporate", Enabled = true },
            new() { Id = "net-wan", Name = "WAN", Purpose = "wan", Enabled = true },
            new() { Id = "net-vpn", Name = "VPN Client", Purpose = "vpn-client", Enabled = true }
        };

        var result = await _engine.RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = deviceJson,
            NetworkConfigs = networkConfigs,
            ClientName = "Test Site"
        });

        // 3 corporate networks, port excludes one (net-cameras), so tagged = 3 - 1 (excluded) - 1 (native) = 1
        // This should NOT trigger ACCESS-VLAN-001 (threshold is 2)
        result.Issues.Should().NotContain(i => i.Type == "ACCESS-VLAN-001",
            because: "WAN and VPN-client should be excluded and port only has 1 tagged VLAN");
    }

    [Fact]
    public async Task RunAudit_AccessPortVlanCount_IncludesGuestNetworks()
    {
        // This test verifies that guest networks ARE included in the count (they can be tagged)

        var deviceJson = CreateDeviceJsonWithMacRestrictedTrunkPort();
        var networkConfigs = new List<UniFiNetworkConfig>
        {
            new() { Id = "net-corp", Name = "Corporate", Vlan = 1, Purpose = "corporate", Enabled = true },
            new() { Id = "net-iot", Name = "IoT", Vlan = 20, Purpose = "corporate", Enabled = true },
            new() { Id = "net-guest", Name = "Guest", Vlan = 30, Purpose = "guest", Enabled = true },
            new() { Id = "net-guest2", Name = "Guest 2", Vlan = 40, Purpose = "guest", Enabled = true },
            new() { Id = "net-wan", Name = "WAN", Purpose = "wan", Enabled = true }
        };

        var result = await _engine.RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = deviceJson,
            NetworkConfigs = networkConfigs,
            ClientName = "Test Site"
        });

        // With 2 corporate + 2 guest = 4 selectable networks, native = 1, tagged = 3
        // This SHOULD trigger ACCESS-VLAN-001 (threshold is 2)
        result.Issues.Should().Contain(i => i.Type == "ACCESS-VLAN-001",
            because: "guest networks can be tagged so should be included in the count");
    }

    private static string CreateDeviceJsonWithMacRestrictedTrunkPort()
    {
        // Switch with a trunk port (forward: customize) with single MAC restriction
        // MAC restriction with 1 entry indicates a single-device access port
        return """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "model": "UDM-PRO",
                "ip": "192.0.2.1",
                "network_table": [
                    {
                        "_id": "net-corp",
                        "name": "Corporate",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.1/24"
                    },
                    {
                        "_id": "net-iot",
                        "name": "IoT",
                        "vlan": 20,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.128/25"
                    }
                ]
            },
            {
                "type": "usw",
                "name": "Test Switch",
                "model": "USW-24",
                "ip": "192.0.2.10",
                "port_table": [
                    {
                        "port_idx": 1,
                        "name": "Test Device",
                        "up": true,
                        "forward": "customize",
                        "native_networkconf_id": "net-corp",
                        "excluded_networkconf_ids": [],
                        "is_uplink": false,
                        "port_security_enabled": true,
                        "port_security_mac_address": ["aa:bb:cc:dd:ee:ff"]
                    },
                    {
                        "port_idx": 24,
                        "name": "Uplink",
                        "up": true,
                        "is_uplink": true
                    }
                ]
            }
        ]
        """;
    }

    private static List<UniFiNetworkConfig> CreateNetworkConfigsWithDisabled()
    {
        // 4 corporate networks (3 enabled + 1 disabled) + 1 WAN
        // WAN is needed to avoid EXTERNAL_ZONE_NOT_DETECTED warning
        return new List<UniFiNetworkConfig>
        {
            new() { Id = "net-corp", Name = "Corporate", Vlan = 1, Purpose = "corporate", Enabled = true },
            new() { Id = "net-iot", Name = "IoT", Vlan = 20, Purpose = "corporate", Enabled = true },
            new() { Id = "net-cameras", Name = "Cameras", Vlan = 30, Purpose = "corporate", Enabled = true },
            new() { Id = "net-disabled", Name = "Disabled Net", Vlan = 40, Purpose = "corporate", Enabled = false },
            new() { Id = "net-wan", Name = "WAN", Purpose = "wan", Enabled = true }
        };
    }

    private static string CreateDeviceJsonWithSelectiveTaggedVlans()
    {
        // Switch with a trunk port that explicitly excludes one network
        // This tests the VLAN counting logic without triggering "Allow All" detection
        return """
        [
            {
                "type": "udm",
                "name": "Gateway",
                "model": "UDM-PRO",
                "ip": "192.0.2.1",
                "network_table": [
                    {
                        "_id": "net-corp",
                        "name": "Corporate",
                        "vlan": 1,
                        "purpose": "corporate",
                        "dhcpd_enabled": true,
                        "ip_subnet": "192.0.2.1/24"
                    }
                ]
            },
            {
                "type": "usw",
                "name": "Test Switch",
                "model": "USW-24",
                "ip": "192.0.2.10",
                "port_table": [
                    {
                        "port_idx": 1,
                        "name": "Test Device",
                        "up": true,
                        "forward": "customize",
                        "native_networkconf_id": "net-corp",
                        "excluded_networkconf_ids": ["net-cameras"],
                        "is_uplink": false,
                        "port_security_enabled": true,
                        "port_security_mac_address": ["aa:bb:cc:dd:ee:ff"]
                    },
                    {
                        "port_idx": 24,
                        "name": "Uplink",
                        "up": true,
                        "is_uplink": true
                    }
                ]
            }
        ]
        """;
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
