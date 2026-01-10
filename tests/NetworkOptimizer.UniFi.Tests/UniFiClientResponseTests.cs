using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class UniFiClientResponseTests
{
    #region EffectiveNetworkId Tests

    [Fact]
    public void EffectiveNetworkId_WhenNoOverride_ReturnsNetworkId()
    {
        // Arrange
        var client = new UniFiClientResponse
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
        var client = new UniFiClientResponse
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
        var client = new UniFiClientResponse
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
        var client = new UniFiClientResponse
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
        // Arrange - Override ID set but not enabled (shouldn't happen but handle it)
        var client = new UniFiClientResponse
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
        var client = new UniFiClientResponse();

        // Act & Assert
        client.Vlan.Should().BeNull();
    }

    [Fact]
    public void Vlan_CanBeSet()
    {
        // Arrange
        var client = new UniFiClientResponse
        {
            Vlan = 5
        };

        // Act & Assert
        client.Vlan.Should().Be(5);
    }

    #endregion

    #region VirtualNetworkOverride Property Tests

    [Fact]
    public void VirtualNetworkOverrideEnabled_DefaultsToFalse()
    {
        // Arrange
        var client = new UniFiClientResponse();

        // Act & Assert
        client.VirtualNetworkOverrideEnabled.Should().BeFalse();
    }

    [Fact]
    public void VirtualNetworkOverrideId_DefaultsToNull()
    {
        // Arrange
        var client = new UniFiClientResponse();

        // Act & Assert
        client.VirtualNetworkOverrideId.Should().BeNull();
    }

    #endregion

    #region Real-World Scenario Tests

    [Fact]
    public void EffectiveNetworkId_ReolinkCameraOnIOTSsidWithCamerasOverride()
    {
        // Arrange - Simulates the bug scenario from sta.txt
        // Camera connected to WhyFi-IOT SSID but overridden to Cameras network
        var client = new UniFiClientResponse
        {
            Mac = "6c:30:2a:3a:fd:0c",
            Name = "Backyard Camera",
            Hostname = "Reolink",
            Ip = "10.5.0.32",
            Network = "IOT",  // SSID's native network
            NetworkId = "6960703944205638894a8db4",  // IOT network ID
            VirtualNetworkOverrideEnabled = true,
            VirtualNetworkOverrideId = "6953bb5073e8980d90f86982",  // Cameras network ID
            Vlan = 5
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("6953bb5073e8980d90f86982");
        client.NetworkId.Should().Be("6960703944205638894a8db4");
        client.Vlan.Should().Be(5);
    }

    [Fact]
    public void EffectiveNetworkId_CameraWithMatchingNetworkIdAndOverride()
    {
        // Arrange - Simulates East Floodlights where network_id already matches override
        var client = new UniFiClientResponse
        {
            Mac = "28:7b:11:36:78:d6",
            Name = "East Floodlights",
            Ip = "10.5.0.70",
            Network = "Cameras",
            NetworkId = "6953bb5073e8980d90f86982",  // Already Cameras
            VirtualNetworkOverrideEnabled = true,
            VirtualNetworkOverrideId = "6953bb5073e8980d90f86982",  // Also Cameras
            Vlan = 5
        };

        // Act & Assert - Both should return the same ID
        client.EffectiveNetworkId.Should().Be("6953bb5073e8980d90f86982");
        client.NetworkId.Should().Be("6953bb5073e8980d90f86982");
    }

    [Fact]
    public void EffectiveNetworkId_SimpliSafeCameraNoOverride()
    {
        // Arrange - Cloud camera without override (should stay on Default)
        var client = new UniFiClientResponse
        {
            Mac = "08:fb:ea:15:c4:38",
            Name = "SimpliSafe Camera",
            Ip = "10.1.0.232",
            Network = "Default",
            NetworkId = "66cb80d92c34a36d7e34d7c3",
            VirtualNetworkOverrideEnabled = false,
            VirtualNetworkOverrideId = null,
            Vlan = null
        };

        // Act & Assert
        client.EffectiveNetworkId.Should().Be("66cb80d92c34a36d7e34d7c3");
    }

    #endregion
}
