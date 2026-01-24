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

    #region BestIp Fallback Tests (UX/UX7 workaround)

    [Fact]
    public void BestIp_WhenIpSet_ReturnsIp()
    {
        // Arrange - Normal client with IP
        var client = new UniFiClientResponse
        {
            Ip = "10.0.0.100",
            LastIp = "10.0.0.200",
            FixedIp = "10.0.0.50"
        };

        // Act & Assert - Should prefer Ip
        client.BestIp.Should().Be("10.0.0.100");
    }

    [Fact]
    public void BestIp_WhenIpEmptyAndLastIpSet_ReturnsLastIp()
    {
        // Arrange - UX/UX7 client missing Ip but has LastIp
        var client = new UniFiClientResponse
        {
            Ip = "",
            LastIp = "10.0.0.200",
            FixedIp = "10.0.0.50"
        };

        // Act & Assert - Should fall back to LastIp
        client.BestIp.Should().Be("10.0.0.200");
    }

    [Fact]
    public void BestIp_WhenIpNullAndLastIpSet_ReturnsLastIp()
    {
        // Arrange - UX/UX7 client with null Ip
        var client = new UniFiClientResponse
        {
            LastIp = "10.0.0.200",
            FixedIp = "10.0.0.50"
        };

        // Act & Assert - Should fall back to LastIp
        client.BestIp.Should().Be("10.0.0.200");
    }

    [Fact]
    public void BestIp_WhenIpAndLastIpEmpty_ReturnsFixedIp()
    {
        // Arrange - Client with only FixedIp
        var client = new UniFiClientResponse
        {
            Ip = "",
            LastIp = "",
            FixedIp = "10.0.0.50"
        };

        // Act & Assert - Should fall back to FixedIp
        client.BestIp.Should().Be("10.0.0.50");
    }

    [Fact]
    public void BestIp_WhenAllEmpty_ReturnsNull()
    {
        // Arrange - Client with no IP at all (would need /clients/active enrichment)
        var client = new UniFiClientResponse
        {
            Ip = "",
            LastIp = null,
            FixedIp = null
        };

        // Act & Assert
        client.BestIp.Should().BeNull();
    }

    [Fact]
    public void BestIp_DefaultClient_ReturnsNull()
    {
        // Arrange - New client with default values
        var client = new UniFiClientResponse();

        // Act & Assert
        client.BestIp.Should().BeNull();
    }

    [Fact]
    public void BestIp_UxClientScenario_FallsBackToLastIp()
    {
        // Arrange - Simulates UX/UX7 connected client from GitHub issue #141
        // stat/sta returns empty Ip but has LastIp
        var client = new UniFiClientResponse
        {
            Mac = "00:5b:94:a8:50:a1",
            Hostname = "TestDevice",
            Ip = "",  // Empty due to UX/UX7 API bug
            LastIp = "10.0.0.137",  // But last_ip is populated
            FixedIp = null
        };

        // Act & Assert - Should get IP from LastIp without needing /clients/active call
        client.BestIp.Should().Be("10.0.0.137");
    }

    #endregion
}
