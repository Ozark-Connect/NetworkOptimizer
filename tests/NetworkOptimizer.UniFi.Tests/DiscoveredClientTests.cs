using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class DiscoveredClientTests
{
    #region EffectiveNetworkId Tests

    [Fact]
    public void EffectiveNetworkId_WhenNoOverride_ReturnsNetworkId()
    {
        // Arrange
        var client = new DiscoveredClient
        {
            NetworkId = "iot-network-id",
            VirtualNetworkOverrideEnabled = false,
            VirtualNetworkOverrideId = null
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("iot-network-id");
    }

    [Fact]
    public void EffectiveNetworkId_WhenOverrideEnabled_ReturnsOverrideId()
    {
        // Arrange
        var client = new DiscoveredClient
        {
            NetworkId = "iot-network-id",
            VirtualNetworkOverrideEnabled = true,
            VirtualNetworkOverrideId = "cameras-network-id"
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("cameras-network-id");
    }

    [Fact]
    public void EffectiveNetworkId_WhenOverrideEnabledButIdNull_ReturnsNetworkId()
    {
        // Arrange - Edge case: override enabled but no ID set
        var client = new DiscoveredClient
        {
            NetworkId = "iot-network-id",
            VirtualNetworkOverrideEnabled = true,
            VirtualNetworkOverrideId = null
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("iot-network-id");
    }

    [Fact]
    public void EffectiveNetworkId_WhenOverrideEnabledButIdEmpty_ReturnsNetworkId()
    {
        // Arrange - Edge case: override enabled but empty ID
        var client = new DiscoveredClient
        {
            NetworkId = "iot-network-id",
            VirtualNetworkOverrideEnabled = true,
            VirtualNetworkOverrideId = ""
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("iot-network-id");
    }

    [Fact]
    public void EffectiveNetworkId_WhenOverrideDisabledAndIdSet_ReturnsNetworkId()
    {
        // Arrange - Override ID set but not enabled
        var client = new DiscoveredClient
        {
            NetworkId = "iot-network-id",
            VirtualNetworkOverrideEnabled = false,
            VirtualNetworkOverrideId = "cameras-network-id"
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("iot-network-id");
    }

    #endregion

    #region Vlan Property Tests

    [Fact]
    public void Vlan_DefaultsToNull()
    {
        // Arrange
        var client = new DiscoveredClient();

        // Act & Assert
        client.Vlan.Should().BeNull();
    }

    [Fact]
    public void Vlan_CanBeSet()
    {
        // Arrange
        var client = new DiscoveredClient
        {
            Vlan = 5
        };

        // Act & Assert
        client.Vlan.Should().Be(5);
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public void EffectiveNetworkId_WirelessCameraWithOverride()
    {
        // Arrange - Camera on IOT SSID but overridden to Cameras VLAN
        var client = new DiscoveredClient
        {
            Id = "test-id",
            Mac = "6c:30:2a:3a:fd:0c",
            Name = "Backyard Camera",
            Hostname = "Reolink",
            IpAddress = "10.5.0.32",
            Network = "IOT",
            NetworkId = "iot-network-id",
            VirtualNetworkOverrideEnabled = true,
            VirtualNetworkOverrideId = "cameras-network-id",
            Vlan = 5,
            IsWired = false
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("cameras-network-id");
        client.NetworkId.Should().Be("iot-network-id");
        client.Vlan.Should().Be(5);
    }

    [Fact]
    public void EffectiveNetworkId_WiredDeviceNoOverride()
    {
        // Arrange - Wired device without override
        var client = new DiscoveredClient
        {
            Id = "test-id",
            Mac = "aa:bb:cc:dd:ee:ff",
            Name = "Desktop PC",
            IpAddress = "10.1.0.50",
            Network = "Default",
            NetworkId = "default-network-id",
            VirtualNetworkOverrideEnabled = false,
            VirtualNetworkOverrideId = null,
            Vlan = 1,
            IsWired = true
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("default-network-id");
    }

    #endregion
}
