using FluentAssertions;
using NetworkOptimizer.Diagnostics.Analyzers;
using Xunit;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Tests.Analyzers;

public class TrunkConsistencyAnalyzerTests
{
    private readonly TrunkConsistencyAnalyzer _analyzer;

    public TrunkConsistencyAnalyzerTests()
    {
        _analyzer = new TrunkConsistencyAnalyzer();
    }

    [Fact]
    public void Analyze_EmptyDevices_ReturnsEmptyList()
    {
        // Arrange
        var devices = new List<UniFiDeviceResponse>();
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>();

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_SingleDevice_ReturnsEmptyList()
    {
        // Arrange
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Id = "switch1",
                Mac = "aa:bb:cc:00:00:01",
                Name = "Switch 1",
                Type = "usw",
                PortTable = new List<SwitchPort>()
            }
        };
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "network-1", Name = "Main LAN", Vlan = 1 }
        };

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_DevicesNotConnected_ReturnsEmptyList()
    {
        // Arrange - two switches with no uplink relationship
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Id = "switch1",
                Mac = "aa:bb:cc:00:00:01",
                Name = "Switch 1",
                Type = "usw",
                PortTable = new List<SwitchPort>()
            },
            new UniFiDeviceResponse
            {
                Id = "switch2",
                Mac = "aa:bb:cc:00:00:02",
                Name = "Switch 2",
                Type = "usw",
                PortTable = new List<SwitchPort>()
            }
        };
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "network-1", Name = "Main LAN", Vlan = 1 }
        };

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_EmptyNetworks_ReturnsEmptyList()
    {
        // Arrange
        var devices = new List<UniFiDeviceResponse>();
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>();

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_EmptyPortProfiles_HandlesGracefully()
    {
        // Arrange
        var devices = new List<UniFiDeviceResponse>
        {
            new UniFiDeviceResponse
            {
                Id = "switch1",
                Mac = "aa:bb:cc:00:00:01",
                Name = "Switch 1",
                Type = "usw",
                PortTable = new List<SwitchPort>
                {
                    new SwitchPort { PortIdx = 1, Forward = "all" }
                }
            }
        };
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "network-1", Name = "Main LAN", Vlan = 1 }
        };

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert - should not throw, just return empty since no trunk links
        result.Should().BeEmpty();
    }
}
