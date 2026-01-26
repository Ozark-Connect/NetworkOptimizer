using FluentAssertions;
using NetworkOptimizer.Audit.Services;
using Xunit;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Tests;

public class DiagnosticsEngineTests
{
    private readonly DeviceTypeDetectionService _detectionService;
    private readonly DiagnosticsEngine _engine;

    public DiagnosticsEngineTests()
    {
        _detectionService = new DeviceTypeDetectionService();
        _engine = new DiagnosticsEngine(_detectionService);
    }

    #region Basic Functionality Tests

    [Fact]
    public void RunDiagnostics_EmptyData_ReturnsEmptyResult()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>();
        var devices = new List<UniFiDeviceResponse>();
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>();

        // Act
        var result = _engine.RunDiagnostics(clients, devices, portProfiles, networks);

        // Assert
        result.Should().NotBeNull();
        result.TotalIssueCount.Should().Be(0);
        result.ApLockIssues.Should().BeEmpty();
        result.TrunkConsistencyIssues.Should().BeEmpty();
        result.PortProfileSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void RunDiagnostics_SetsTimestamp()
    {
        // Arrange
        var beforeRun = DateTime.UtcNow;

        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(),
            new List<UniFiDeviceResponse>(),
            new List<UniFiPortProfile>(),
            new List<UniFiNetworkConfig>());

        // Assert
        result.Timestamp.Should().BeOnOrAfter(beforeRun);
        result.Timestamp.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void RunDiagnostics_SetsDuration()
    {
        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(),
            new List<UniFiDeviceResponse>(),
            new List<UniFiPortProfile>(),
            new List<UniFiNetworkConfig>());

        // Assert
        result.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    #endregion

    #region Options Tests

    [Fact]
    public void RunDiagnostics_AllAnalyzersDisabled_ReturnsEmptyResult()
    {
        // Arrange
        var options = new DiagnosticsOptions
        {
            RunApLockAnalyzer = false,
            RunTrunkConsistencyAnalyzer = false,
            RunPortProfileSuggestionAnalyzer = false
        };

        var clients = CreateSampleClients();
        var devices = CreateSampleDevices();
        var portProfiles = new List<UniFiPortProfile>();
        var networks = CreateSampleNetworks();

        // Act
        var result = _engine.RunDiagnostics(clients, devices, portProfiles, networks, options);

        // Assert
        result.ApLockIssues.Should().BeEmpty();
        result.TrunkConsistencyIssues.Should().BeEmpty();
        result.PortProfileSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void RunDiagnostics_OnlyApLockEnabled_RunsOnlyApLock()
    {
        // Arrange
        var options = new DiagnosticsOptions
        {
            RunApLockAnalyzer = true,
            RunTrunkConsistencyAnalyzer = false,
            RunPortProfileSuggestionAnalyzer = false
        };

        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "iPhone",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test AP",
                Type = "uap"
            }
        };

        // Act
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>(), options);

        // Assert
        result.ApLockIssues.Should().NotBeEmpty();
        result.TrunkConsistencyIssues.Should().BeEmpty();
        result.PortProfileSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void RunDiagnostics_DefaultOptions_RunsAllAnalyzers()
    {
        // Arrange - default options should enable all analyzers
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "iPhone",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test AP",
                Type = "uap"
            }
        };

        // Act
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>());

        // Assert - at least AP lock should find an issue
        result.ApLockIssues.Should().NotBeEmpty();
    }

    #endregion

    #region Total Issue Count Tests

    [Fact]
    public void RunDiagnostics_MultipleIssues_CalculatesTotalCorrectly()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "iPhone 1",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            },
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:02",
                Name = "iPhone 2",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test AP",
                Type = "uap"
            }
        };

        // Act
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>());

        // Assert
        result.ApLockIssues.Should().HaveCount(2);
        result.TotalIssueCount.Should().Be(2);
    }

    #endregion

    #region Warning Count Tests

    [Fact]
    public void RunDiagnostics_MobileDevicesLocked_CountsWarnings()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "iPhone",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            },
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:02",
                Name = "Ring Doorbell", // Stationary - should be Info
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test AP",
                Type = "uap"
            }
        };

        // Act
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>());

        // Assert
        result.WarningCount.Should().Be(1); // Only iPhone is a warning
        result.TotalIssueCount.Should().Be(2); // Both are issues
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void RunDiagnostics_NullOptions_UsesDefaults()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>();
        var devices = new List<UniFiDeviceResponse>();

        // Act - passing null options should work
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>(), null);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region Helper Methods

    private static List<UniFiClientResponse> CreateSampleClients()
    {
        return new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Test Client",
                IsWired = true
            }
        };
    }

    private static List<UniFiDeviceResponse> CreateSampleDevices()
    {
        return new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test Switch",
                Type = "usw",
                PortTable = new List<SwitchPort>()
            }
        };
    }

    private static List<UniFiNetworkConfig> CreateSampleNetworks()
    {
        return new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig
            {
                Id = "network-1",
                Name = "Main LAN",
                Vlan = 1
            }
        };
    }

    #endregion
}
