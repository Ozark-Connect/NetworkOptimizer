using FluentAssertions;
using NetworkOptimizer.Diagnostics.Analyzers;
using Xunit;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Tests.Analyzers;

public class PortProfileSuggestionAnalyzerTests
{
    private readonly PortProfileSuggestionAnalyzer _analyzer;

    public PortProfileSuggestionAnalyzerTests()
    {
        _analyzer = new PortProfileSuggestionAnalyzer();
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
    public void Analyze_NoTrunkPorts_ReturnsEmptyList()
    {
        // Arrange - device with only access ports
        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort { PortIdx = 1, Forward = "native", NativeNetworkConfId = "network-1" },
                new SwitchPort { PortIdx = 2, Forward = "native", NativeNetworkConfId = "network-1" }
            }
        };

        var devices = new List<UniFiDeviceResponse> { device };
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
    public void Analyze_SingleTrunkPort_ReturnsEmptyList()
    {
        // Arrange - only one trunk port, need at least 2 with same config
        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort { PortIdx = 1, Forward = "all" }
            }
        };

        var devices = new List<UniFiDeviceResponse> { device };
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "network-1", Name = "Main LAN", Vlan = 1 }
        };

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert - need at least 2 ports with same config
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
        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort { PortIdx = 1, Forward = "all" }
            }
        };

        var devices = new List<UniFiDeviceResponse> { device };
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "network-1", Name = "Main LAN", Vlan = 1 }
        };

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert - should not throw
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_DeviceWithNullPortTable_HandlesGracefully()
    {
        // Arrange
        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = null
        };

        var devices = new List<UniFiDeviceResponse> { device };
        var portProfiles = new List<UniFiPortProfile>();
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "network-1", Name = "Main LAN", Vlan = 1 }
        };

        // Act
        var result = _analyzer.Analyze(devices, portProfiles, networks);

        // Assert - should not throw
        result.Should().BeEmpty();
    }
}
