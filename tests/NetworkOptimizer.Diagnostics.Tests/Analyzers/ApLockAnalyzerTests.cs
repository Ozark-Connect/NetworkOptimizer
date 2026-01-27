using FluentAssertions;
using NetworkOptimizer.Audit.Services;
using Xunit;
using NetworkOptimizer.Diagnostics.Analyzers;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Tests.Analyzers;

public class ApLockAnalyzerTests
{
    private readonly DeviceTypeDetectionService _detectionService;
    private readonly ApLockAnalyzer _analyzer;

    public ApLockAnalyzerTests()
    {
        _detectionService = new DeviceTypeDetectionService();
        _analyzer = new ApLockAnalyzer(_detectionService);
    }

    [Fact]
    public void Analyze_EmptyClients_ReturnsEmptyList()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>();
        var devices = new List<UniFiDeviceResponse>();

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_NoLockedClients_ReturnsEmptyList()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Test Device",
                IsWired = false,
                FixedApEnabled = false
            }
        };
        var devices = new List<UniFiDeviceResponse>();

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_WiredClient_IsIgnored()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Wired Device",
                IsWired = true,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>();

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_LockedClientWithoutApMac_IsIgnored()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Test Device",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = null
            }
        };
        var devices = new List<UniFiDeviceResponse>();

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_LockedWirelessClient_ReturnsIssue()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Test Device",
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().HaveCount(1);
        result[0].ClientMac.Should().Be("aa:bb:cc:dd:ee:01");
        result[0].LockedApMac.Should().Be("00:11:22:33:44:55");
        result[0].LockedApName.Should().Be("Test AP");
    }

    [Fact]
    public void Analyze_ApNotFound_ShowsUnknownAp()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Test Device",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>(); // No APs

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().HaveCount(1);
        result[0].LockedApName.Should().Be("Unknown AP");
    }

    [Fact]
    public void Analyze_MultipleLockedClients_ReturnsAllIssues()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Device 1",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            },
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:02",
                Name = "Device 2",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            },
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:03",
                Name = "Not Locked",
                IsWired = false,
                FixedApEnabled = false
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_ClientWithRoamCount_IncludesRoamCountInResult()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Test Device",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55",
                RoamCount = 15
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().HaveCount(1);
        result[0].RoamCount.Should().Be(15);
    }

    #region Severity Tests

    [Fact]
    public void Analyze_MobileDevice_ReturnsSeverityWarning()
    {
        // Arrange - iPhone is a mobile device
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
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert - mobile device locked to AP should be a warning
        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(ApLockSeverity.Warning);
    }

    [Fact]
    public void Analyze_StationaryDevice_ReturnsSeverityInfo()
    {
        // Arrange - Ring Doorbell is a stationary device
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Ring Doorbell",
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert - stationary device locked to AP is fine (info)
        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(ApLockSeverity.Info);
    }

    [Fact]
    public void Analyze_UnknownDeviceHighRoamCount_ReturnsSeverityWarning()
    {
        // Arrange - unknown device with high roam count suggests mobile
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Unknown-Device-XYZ",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55",
                RoamCount = 25 // High roam count
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert - high roam count suggests mobile, should be warning
        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(ApLockSeverity.Warning);
    }

    [Fact]
    public void Analyze_UnknownDeviceLowRoamCount_ReturnsSeverityUnknown()
    {
        // Arrange - unknown device with low roam count
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Unknown-Device-XYZ",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55",
                RoamCount = 2 // Low roam count
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert - can't determine if this is appropriate
        result.Should().HaveCount(1);
        result[0].Severity.Should().Be(ApLockSeverity.Unknown);
    }

    #endregion

    #region Client Name Resolution Tests

    [Fact]
    public void Analyze_ClientWithName_UsesName()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "My iPhone",
                Hostname = "iphone-xyz",
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert - should use Name over Hostname
        result.Should().HaveCount(1);
        result[0].ClientName.Should().Be("My iPhone");
    }

    [Fact]
    public void Analyze_ClientWithOnlyHostname_UsesHostname()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = null!, // Intentionally null to test fallback
                Hostname = "iphone-xyz",
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert - should fall back to Hostname
        result.Should().HaveCount(1);
        result[0].ClientName.Should().Be("iphone-xyz");
    }

    [Fact]
    public void Analyze_ClientWithNoNameOrHostname_UsesMac()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = null!, // Intentionally null to test fallback
                Hostname = null!, // Intentionally null to test fallback
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert - should fall back to MAC address
        result.Should().HaveCount(1);
        result[0].ClientName.Should().Be("aa:bb:cc:dd:ee:01");
    }

    #endregion

    #region Offline Client Tests

    [Fact]
    public void AnalyzeOfflineClients_EmptyHistory_ReturnsEmpty()
    {
        // Arrange
        var historyClients = new List<UniFiClientDetailResponse>();
        var devices = new List<UniFiDeviceResponse>();
        var onlineMacs = new HashSet<string>();

        // Act
        var result = _analyzer.AnalyzeOfflineClients(historyClients, devices, onlineMacs);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeOfflineClients_LockedOfflineClient_ReturnsIssue()
    {
        // Arrange
        var historyClients = new List<UniFiClientDetailResponse>
        {
            new UniFiClientDetailResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Offline iPhone",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55",
                LastSeen = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };
        var onlineMacs = new HashSet<string>(); // Client is not online

        // Act
        var result = _analyzer.AnalyzeOfflineClients(historyClients, devices, onlineMacs);

        // Assert
        result.Should().HaveCount(1);
        result[0].ClientMac.Should().Be("aa:bb:cc:dd:ee:01");
        result[0].IsOffline.Should().BeTrue();
        result[0].LastSeen.Should().NotBeNull();
    }

    [Fact]
    public void AnalyzeOfflineClients_ClientCurrentlyOnline_IsExcluded()
    {
        // Arrange
        var historyClients = new List<UniFiClientDetailResponse>
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
        var onlineMacs = new HashSet<string> { "aa:bb:cc:dd:ee:01" }; // Client IS online

        // Act
        var result = _analyzer.AnalyzeOfflineClients(historyClients, devices, onlineMacs);

        // Assert - should exclude since client is online
        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeOfflineClients_WiredClient_IsExcluded()
    {
        // Arrange - wired clients should be excluded
        var historyClients = new List<UniFiClientDetailResponse>
        {
            new UniFiClientDetailResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Wired Device",
                IsWired = true,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };
        var onlineMacs = new HashSet<string>();

        // Act
        var result = _analyzer.AnalyzeOfflineClients(historyClients, devices, onlineMacs);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeOfflineClients_ClientWithDisplayName_UsesDisplayName()
    {
        // Arrange
        var historyClients = new List<UniFiClientDetailResponse>
        {
            new UniFiClientDetailResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                DisplayName = "User's iPhone",
                Name = "iPhone",
                Hostname = "iphone-xyz",
                IsWired = false,
                FixedApEnabled = true,
                FixedApMac = "00:11:22:33:44:55"
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };
        var onlineMacs = new HashSet<string>();

        // Act
        var result = _analyzer.AnalyzeOfflineClients(historyClients, devices, onlineMacs);

        // Assert - should prefer DisplayName
        result.Should().HaveCount(1);
        result[0].ClientName.Should().Be("User's iPhone");
    }

    #endregion

    #region Recommendation Tests

    [Fact]
    public void Analyze_MobileDevice_RecommendationSuggestsRemovingLock()
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
                FixedApMac = "00:11:22:33:44:55",
                RoamCount = 10
            }
        };
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse { Mac = "00:11:22:33:44:55", Name = "Test AP", Type = "uap" }
        };

        // Act
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().Contain("roam");
        result[0].Recommendation.Should().Contain("removing the AP lock");
    }

    [Fact]
    public void Analyze_StationaryDevice_RecommendationConfirmsLockIsAppropriate()
    {
        // Arrange
        var clients = new List<UniFiClientResponse>
        {
            new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:01",
                Name = "Ring Doorbell",
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
        var result = _analyzer.Analyze(clients, devices);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().Contain("appropriate");
        result[0].Recommendation.Should().Contain("stationary");
    }

    #endregion
}
