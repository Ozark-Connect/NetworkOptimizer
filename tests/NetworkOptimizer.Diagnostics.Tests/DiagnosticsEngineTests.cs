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

    #region Client History Tests

    [Fact]
    public void RunDiagnostics_WithClientHistory_AnalyzesOfflineClients()
    {
        // Arrange
        var onlineClients = new List<UniFiClientResponse>(); // No online clients
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test AP",
                Type = "uap"
            }
        };

        // Historical client that's now offline and has AP lock
        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new UniFiClientDetailResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "iPhone",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };

        // Act
        var result = _engine.RunDiagnostics(
            onlineClients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>(),
            clientHistory: clientHistory);

        // Assert
        result.ApLockIssues.Should().HaveCount(1);
        result.ApLockIssues[0].ClientMac.Should().Be("aa:bb:cc:dd:ee:01");
        result.ApLockIssues[0].IsOffline.Should().BeTrue();
    }

    [Fact]
    public void RunDiagnostics_WithClientHistory_SkipsOnlineClients()
    {
        // Arrange - same client is both online and in history
        var onlineClients = new List<UniFiClientResponse>
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

        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new UniFiClientDetailResponse
            {
                Mac = "aa:bb:cc:dd:ee:01", // Same as online
                Name = "iPhone",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };

        // Act
        var result = _engine.RunDiagnostics(
            onlineClients, devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>(),
            clientHistory: clientHistory);

        // Assert - should only count once (online client)
        result.ApLockIssues.Should().HaveCount(1);
        result.ApLockIssues[0].IsOffline.Should().BeFalse();
    }

    [Fact]
    public void RunDiagnostics_EmptyClientHistory_DoesNotThrow()
    {
        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(),
            new List<UniFiDeviceResponse>(),
            new List<UniFiPortProfile>(),
            new List<UniFiNetworkConfig>(),
            clientHistory: new List<UniFiClientDetailResponse>());

        // Assert
        result.Should().NotBeNull();
        result.ApLockIssues.Should().BeEmpty();
    }

    [Fact]
    public void RunDiagnostics_NullClientHistory_DoesNotThrow()
    {
        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(),
            new List<UniFiDeviceResponse>(),
            new List<UniFiPortProfile>(),
            new List<UniFiNetworkConfig>(),
            clientHistory: null);

        // Assert
        result.Should().NotBeNull();
        result.ApLockIssues.Should().BeEmpty();
    }

    [Fact]
    public void RunDiagnostics_ApLockDisabled_DoesNotAnalyzeHistory()
    {
        // Arrange
        var options = new DiagnosticsOptions
        {
            RunApLockAnalyzer = false
        };

        var clientHistory = new List<UniFiClientDetailResponse>
        {
            new UniFiClientDetailResponse
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
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };

        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(), devices, new List<UniFiPortProfile>(), new List<UniFiNetworkConfig>(),
            options, clientHistory);

        // Assert
        result.ApLockIssues.Should().BeEmpty();
    }

    #endregion

    #region Individual Analyzer Disable Tests

    [Fact]
    public void RunDiagnostics_OnlyTrunkConsistencyEnabled_RunsOnlyTrunk()
    {
        // Arrange
        var options = new DiagnosticsOptions
        {
            RunApLockAnalyzer = false,
            RunTrunkConsistencyAnalyzer = true,
            RunPortProfileSuggestionAnalyzer = false
        };

        // Create devices with a trunk mismatch
        var devices = CreateDevicesWithTrunkMismatch();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20, Purpose = "corporate" }
        };

        // Client that would trigger AP lock if enabled
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

        // Act
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), networks, options);

        // Assert
        result.ApLockIssues.Should().BeEmpty();
        result.TrunkConsistencyIssues.Should().NotBeEmpty();
        result.PortProfileSuggestions.Should().BeEmpty();
    }

    [Fact]
    public void RunDiagnostics_OnlyPortProfileEnabled_RunsOnlyPortProfile()
    {
        // Arrange
        var options = new DiagnosticsOptions
        {
            RunApLockAnalyzer = false,
            RunTrunkConsistencyAnalyzer = false,
            RunPortProfileSuggestionAnalyzer = true
        };

        // Create devices with ports that would generate a suggestion
        var devices = CreateDevicesWithSimilarPorts();
        var networks = CreateNetworksForPortProfile();

        // Client that would trigger AP lock if enabled
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

        // Act
        var result = _engine.RunDiagnostics(clients, devices, new List<UniFiPortProfile>(), networks, options);

        // Assert
        result.ApLockIssues.Should().BeEmpty();
        result.TrunkConsistencyIssues.Should().BeEmpty();
        result.PortProfileSuggestions.Should().NotBeEmpty();
    }

    #endregion

    #region Integration Tests - All Analyzers Find Issues

    [Fact]
    public void RunDiagnostics_TrunkMismatch_FindsIssue()
    {
        // Arrange
        var devices = CreateDevicesWithTrunkMismatch();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10 },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20 }
        };

        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(), devices, new List<UniFiPortProfile>(), networks);

        // Assert
        result.TrunkConsistencyIssues.Should().NotBeEmpty();
    }

    [Fact]
    public void RunDiagnostics_SimilarPorts_GeneratesSuggestion()
    {
        // Arrange - 3+ ports with same VLAN config and no profile
        var devices = CreateDevicesWithSimilarPorts();
        var networks = CreateNetworksForPortProfile();

        // Act
        var result = _engine.RunDiagnostics(
            new List<UniFiClientResponse>(), devices, new List<UniFiPortProfile>(), networks);

        // Assert
        result.PortProfileSuggestions.Should().NotBeEmpty();
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

    private static List<UniFiDeviceResponse> CreateDevicesWithTrunkMismatch()
    {
        // Two switches connected via trunk, but with mismatched VLANs
        // A trunk port requires Forward = "customize" and TaggedVlanMgmt = "custom"
        return new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Switch 1",
                Type = "usw",
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort
                    {
                        PortIdx = 1,
                        Forward = "customize",
                        TaggedVlanMgmt = "custom",
                        ExcludedNetworkConfIds = new List<string>(), // Allows net-2
                        IsUplink = false // Upstream port
                    }
                }
            },
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:66",
                Name = "Switch 2",
                Type = "usw",
                Uplink = new UplinkInfo { UplinkMac = "00:11:22:33:44:55", UplinkRemotePort = 1 },
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort
                    {
                        PortIdx = 1,
                        Forward = "customize",
                        TaggedVlanMgmt = "custom",
                        ExcludedNetworkConfIds = new List<string> { "net-2" }, // Excludes net-2 = mismatch!
                        IsUplink = true // Downstream port uplinks here
                    }
                }
            }
        };
    }

    private static List<UniFiDeviceResponse> CreateDevicesWithSimilarPorts()
    {
        // Switch with 3 trunk ports that have identical config but no profile
        // A trunk port requires Forward = "customize" and TaggedVlanMgmt = "custom"
        return new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Mac = "00:11:22:33:44:55",
                Name = "Test Switch",
                Type = "usw",
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort
                    {
                        PortIdx = 1,
                        Forward = "customize",
                        TaggedVlanMgmt = "custom",
                        NativeNetworkConfId = "net-1",
                        ExcludedNetworkConfIds = new List<string>(),
                        PortConfId = null, // No profile
                        Speed = 1000,
                        Autoneg = true
                    },
                    new SwitchPort
                    {
                        PortIdx = 2,
                        Forward = "customize",
                        TaggedVlanMgmt = "custom",
                        NativeNetworkConfId = "net-1",
                        ExcludedNetworkConfIds = new List<string>(),
                        PortConfId = null, // No profile
                        Speed = 1000,
                        Autoneg = true
                    },
                    new SwitchPort
                    {
                        PortIdx = 3,
                        Forward = "customize",
                        TaggedVlanMgmt = "custom",
                        NativeNetworkConfId = "net-1",
                        ExcludedNetworkConfIds = new List<string>(),
                        PortConfId = null, // No profile
                        Speed = 1000,
                        Autoneg = true
                    }
                }
            }
        };
    }

    private static List<UniFiNetworkConfig> CreateNetworksForPortProfile()
    {
        return new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig
            {
                Id = "net-1",
                Name = "Main LAN",
                Vlan = 1
            }
        };
    }

    #endregion
}
