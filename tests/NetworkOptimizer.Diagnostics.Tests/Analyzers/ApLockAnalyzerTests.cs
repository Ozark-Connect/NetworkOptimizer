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
}
