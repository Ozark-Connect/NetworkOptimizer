using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for NetworkPath and PathAnalysisResult models.
/// These tests verify path properties and analysis logic without mocking the full NetworkPathAnalyzer.
/// </summary>
public class NetworkPathAnalyzerTests
{
    #region HasWirelessConnection Tests

    [Fact]
    public void HasWirelessConnection_WiredPath_ReturnsFalse()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessConnection_WirelessClientPath_ReturnsTrue()
    {
        // Arrange
        var path = NetworkTestData.CreateWirelessClientPath();

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_MeshClientPath_ReturnsTrue()
    {
        // Arrange
        var path = NetworkTestData.CreateMeshClientPath();

        // Act & Assert - Both client->AP and AP->AP segments are wireless
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_ClientFollowedByAP_ReturnsTrue()
    {
        // Arrange - Minimal path with just Client -> AP
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new NetworkHop { Type = HopType.Client },
                new NetworkHop { Type = HopType.AccessPoint }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    [Fact]
    public void HasWirelessConnection_APFollowedBySwitch_ReturnsFalse()
    {
        // Arrange - AP with wired uplink (no wireless connection)
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new NetworkHop { Type = HopType.AccessPoint },
                new NetworkHop { Type = HopType.Switch }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessConnection_APFollowedByAP_ReturnsTrue()
    {
        // Arrange - Mesh backhaul (AP -> AP is wireless)
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new NetworkHop { Type = HopType.AccessPoint },
                new NetworkHop { Type = HopType.AccessPoint }
            }
        };

        // Act & Assert
        path.HasWirelessConnection.Should().BeTrue();
    }

    #endregion

    #region HasWirelessSegment Tests

    [Fact]
    public void HasWirelessSegment_WiredPath_ReturnsFalse()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();

        // Act & Assert
        path.HasWirelessSegment.Should().BeFalse();
    }

    [Fact]
    public void HasWirelessSegment_PathWithAP_ReturnsTrue()
    {
        // Arrange
        var path = NetworkTestData.CreateWirelessClientPath();

        // Act & Assert
        path.HasWirelessSegment.Should().BeTrue();
    }

    #endregion

    #region SwitchHopCount Tests

    [Fact]
    public void SwitchHopCount_SingleSwitch_ReturnsOne()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();

        // Act & Assert
        path.SwitchHopCount.Should().Be(1);
    }

    [Fact]
    public void SwitchHopCount_NoSwitches_ReturnsZero()
    {
        // Arrange
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new NetworkHop { Type = HopType.Client },
                new NetworkHop { Type = HopType.Gateway },
                new NetworkHop { Type = HopType.Server }
            }
        };

        // Act & Assert
        path.SwitchHopCount.Should().Be(0);
    }

    #endregion

    #region PathAnalysisResult - Efficiency Tests

    [Fact]
    public void CalculateEfficiency_ValidPath_CalculatesCorrectly()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 850
        };

        // Act
        result.CalculateEfficiency();

        // Assert
        result.FromDeviceEfficiencyPercent.Should().Be(90);
        result.ToDeviceEfficiencyPercent.Should().Be(85);
    }

    [Fact]
    public void CalculateEfficiency_ZeroRealisticMax_DoesNotThrow()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 0 },
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 850
        };

        // Act
        var act = () => result.CalculateEfficiency();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region PathAnalysisResult - Grade Tests

    [Theory]
    [InlineData(95, PerformanceGrade.Excellent)]
    [InlineData(90, PerformanceGrade.Excellent)]
    [InlineData(89, PerformanceGrade.Good)]
    [InlineData(75, PerformanceGrade.Good)]
    [InlineData(74, PerformanceGrade.Fair)]
    [InlineData(50, PerformanceGrade.Fair)]
    [InlineData(49, PerformanceGrade.Poor)]
    [InlineData(25, PerformanceGrade.Poor)]
    [InlineData(24, PerformanceGrade.Critical)]
    [InlineData(0, PerformanceGrade.Critical)]
    public void CalculateEfficiency_AssignsCorrectGrade(double efficiency, PerformanceGrade expectedGrade)
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = efficiency * 10, // 1000 Mbps * efficiency%
            MeasuredToDeviceMbps = efficiency * 10
        };

        // Act
        result.CalculateEfficiency();

        // Assert
        result.FromDeviceGrade.Should().Be(expectedGrade);
        result.ToDeviceGrade.Should().Be(expectedGrade);
    }

    #endregion

    #region PathAnalysisResult - Insights Tests (Regression: 100 Mbps Recommendation)

    [Fact]
    public void GenerateInsights_Wired100Mbps_RecommendsUpgrade()
    {
        // Arrange - Wired path with 100 Mbps bottleneck
        var path = NetworkTestData.CreateWiredClientPath(linkSpeedMbps: 100);
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 90,
            MeasuredToDeviceMbps = 90
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert - Should warn about cable/auto-negotiation for wired 100 Mbps
        result.Recommendations.Should().Contain("10/100 Mbps link detected - cable quality or auto-negotiation may be faulty");
    }

    [Fact]
    public void GenerateInsights_Wireless86Mbps_DoesNotRecommendUpgrade()
    {
        // Arrange - Wireless path with 86 Mbps (normal Wi-Fi speed)
        var path = NetworkTestData.CreateWirelessClientPath(wirelessRateMbps: 86);
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 50,
            MeasuredToDeviceMbps = 50
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert - Should NOT warn about cable for wireless (speeds vary naturally)
        result.Recommendations.Should().NotContain(r => r.Contains("10/100 Mbps link detected"));
    }

    [Fact]
    public void GenerateInsights_MeshPath_DoesNotRecommendGigabitUpgrade()
    {
        // Arrange - Mesh path where backhaul might be slower
        var path = NetworkTestData.CreateMeshClientPath(
            clientWirelessRateMbps: 400,
            meshBackhaulRateMbps: 80);
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 45,
            MeasuredToDeviceMbps = 45
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert - Should NOT warn about cable for mesh (it's wireless)
        result.Recommendations.Should().NotContain(r => r.Contains("10/100 Mbps link detected"));
    }

    [Fact]
    public void GenerateInsights_WirelessPath_AddsWirelessInsight()
    {
        // Arrange
        var path = NetworkTestData.CreateWirelessClientPath();
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain("Path includes wireless segment - speeds may vary with signal quality");
    }

    #endregion

    #region PathAnalysisResult - Gateway Tests

    [Fact]
    public void GenerateInsights_GatewayTarget_AddsGatewayInsight()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();
        path.TargetIsGateway = true;
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain("Gateway speed test - results limited by gateway CPU, not network");
        result.Recommendations.Should().BeEmpty(); // No further recommendations for gateway tests
    }

    [Fact]
    public void GenerateInsights_APTarget_PerformingWell_AddsAPInsight()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();
        path.TargetIsAccessPoint = true;
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 4500, // Above 4400 Mbps threshold
            MeasuredToDeviceMbps = 4500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain("AP speed test - results limited by AP CPU, not network");
    }

    #endregion

    #region PathAnalysisResult - 1 GbE Upgrade Recommendation

    [Fact]
    public void GenerateInsights_Maxing1GbE_RecommendsUpgrade()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath(linkSpeedMbps: 1000);
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 940, // 94% efficiency
            MeasuredToDeviceMbps = 940
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain("Maxing out 1 GbE - consider 2.5G or 10G upgrade for higher speeds");
    }

    #endregion

    #region PathAnalysisResult - Retransmit Analysis

    [Fact]
    public void GenerateInsights_HighRetransmits_AddsPacketLossInsight()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 800,
            MeasuredToDeviceMbps = 800,
            FromDeviceRetransmits = 1000,
            ToDeviceRetransmits = 500,
            FromDeviceBytes = 100_000_000, // ~66,666 packets at 1500 bytes = ~1.5% retransmit rate
            ToDeviceBytes = 100_000_000
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain(i => i.Contains("packet loss"));
    }

    [Fact]
    public void GenerateInsights_WirelessClientRetransmits_RecommendsSignalCheck()
    {
        // Arrange
        var path = NetworkTestData.CreateWirelessClientPath();
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 400,
            MeasuredToDeviceMbps = 400,
            FromDeviceRetransmits = 1000,
            ToDeviceRetransmits = 1000,
            FromDeviceBytes = 50_000_000,
            ToDeviceBytes = 50_000_000
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r => r.Contains("Wi-Fi") && r.Contains("signal"));
    }

    #endregion

    #region Mesh Path Tests (Regression - Session Bugs)

    [Fact]
    public void MeshClientPath_HasCorrectHopCount()
    {
        // Arrange & Act
        var path = NetworkTestData.CreateMeshClientPath();

        // Assert - Should have 6 hops: Client -> MeshAP -> WiredAP -> Switch -> Gateway -> Server
        path.Hops.Should().HaveCount(6);
    }

    [Fact]
    public void MeshClientPath_MeshAPHasWirelessEgress()
    {
        // Arrange
        var path = NetworkTestData.CreateMeshClientPath();

        // Act
        var meshApHop = path.Hops.First(h => h.DeviceName == "AP-Mesh");

        // Assert - Mesh AP should have wireless egress (to wired AP)
        meshApHop.IsWirelessEgress.Should().BeTrue();
        meshApHop.WirelessTxRateMbps.Should().NotBeNull();
        meshApHop.WirelessRxRateMbps.Should().NotBeNull();
    }

    [Fact]
    public void MeshClientPath_WiredAPHasWirelessIngress()
    {
        // Arrange
        var path = NetworkTestData.CreateMeshClientPath();

        // Act
        var wiredApHop = path.Hops.First(h => h.DeviceName == "AP-Wired");

        // Assert - Wired AP should have wireless ingress (from mesh AP)
        wiredApHop.IsWirelessIngress.Should().BeTrue();
        wiredApHop.WirelessIngressBand.Should().Be("na");
    }

    [Fact]
    public void MeshClientPath_ClientHopHasClientSpeed_NotMeshBackhaulSpeed()
    {
        // Arrange - Different speeds for client vs mesh backhaul
        var path = NetworkTestData.CreateMeshClientPath(
            clientWirelessRateMbps: 433, // Client speed
            meshBackhaulRateMbps: 866);  // Mesh backhaul speed

        // Act
        var clientHop = path.Hops.First(h => h.Type == HopType.Client);

        // Assert - Client hop should use client's wireless rate, not mesh backhaul
        clientHop.EgressSpeedMbps.Should().Be(433);
        clientHop.WirelessTxRateMbps.Should().Be(433);
    }

    [Fact]
    public void MeshClientPath_HasTwoAPHops()
    {
        // Arrange
        var path = NetworkTestData.CreateMeshClientPath();

        // Act
        var apHops = path.Hops.Where(h => h.Type == HopType.AccessPoint).ToList();

        // Assert - Should have exactly 2 AP hops (mesh AP and wired AP)
        apHops.Should().HaveCount(2);
    }

    [Fact]
    public void MeshClientPath_APToAPIsWirelessConnection()
    {
        // Arrange
        var path = NetworkTestData.CreateMeshClientPath();

        // Act & Assert - The path should detect AP->AP as wireless connection
        path.HasWirelessConnection.Should().BeTrue();
    }

    #endregion

    #region Bottleneck Tests

    [Fact]
    public void WiredPath_IdentifiesBottleneck()
    {
        // Arrange - Create path with mixed speeds
        var path = new NetworkPath
        {
            Hops = new List<NetworkHop>
            {
                new NetworkHop { Type = HopType.Client, EgressSpeedMbps = 1000 },
                new NetworkHop { Type = HopType.Switch, IngressSpeedMbps = 1000, EgressSpeedMbps = 100, IsBottleneck = true },
                new NetworkHop { Type = HopType.Gateway, IngressSpeedMbps = 100, EgressSpeedMbps = 1000 },
                new NetworkHop { Type = HopType.Server, IngressSpeedMbps = 1000 }
            },
            TheoreticalMaxMbps = 100,
            HasRealBottleneck = true
        };

        // Assert
        path.TheoreticalMaxMbps.Should().Be(100);
        path.HasRealBottleneck.Should().BeTrue();
        path.Hops[1].IsBottleneck.Should().BeTrue();
    }

    [Fact]
    public void WiredPath_AllSameSpeed_NoRealBottleneck()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath(linkSpeedMbps: 1000);
        path.HasRealBottleneck = false; // All links are 1 Gbps

        // Assert
        path.HasRealBottleneck.Should().BeFalse();
    }

    #endregion

    #region Inter-VLAN Routing Tests

    [Fact]
    public void InterVlanPath_RequiresRouting()
    {
        // Arrange
        var path = new NetworkPath
        {
            SourceVlanId = 1,
            SourceNetworkName = "Default",
            DestinationVlanId = 10,
            DestinationNetworkName = "IoT",
            RequiresRouting = true,
            GatewayDevice = "Gateway",
            GatewayModel = "UDM-Pro"
        };

        // Assert
        path.RequiresRouting.Should().BeTrue();
        path.GatewayDevice.Should().Be("Gateway");
    }

    [Fact]
    public void SameVlanPath_DoesNotRequireRouting()
    {
        // Arrange
        var path = NetworkTestData.CreateWiredClientPath();

        // Assert
        path.RequiresRouting.Should().BeFalse();
    }

    #endregion

    #region Device and Client Test Data Validation

    [Fact]
    public void CreateMeshAccessPoint_HasWirelessUplink()
    {
        // Arrange & Act
        var meshAp = NetworkTestData.CreateMeshAccessPoint();

        // Assert
        meshAp.UplinkType.Should().Be("wireless");
        meshAp.UplinkTxRateKbps.Should().BeGreaterThan(0);
        meshAp.UplinkRxRateKbps.Should().BeGreaterThan(0);
        meshAp.UplinkRadioBand.Should().Be("na");
        meshAp.UplinkChannel.Should().Be(36);
        meshAp.UplinkSignalDbm.Should().Be(-55);
    }

    [Fact]
    public void CreateWiredAccessPoint_HasWiredUplink()
    {
        // Arrange & Act
        var wiredAp = NetworkTestData.CreateWiredAccessPoint();

        // Assert
        wiredAp.UplinkType.Should().Be("wire");
        wiredAp.UplinkSpeedMbps.Should().Be(1000);
    }

    [Fact]
    public void CreateMloClient_HasMultipleLinks()
    {
        // Arrange & Act
        var mloClient = NetworkTestData.CreateMloClient();

        // Assert
        mloClient.IsMlo.Should().BeTrue();
        mloClient.MloLinks.Should().HaveCount(3);
        mloClient.MloLinks.Should().Contain(l => l.Radio == "ng");
        mloClient.MloLinks.Should().Contain(l => l.Radio == "na");
        mloClient.MloLinks.Should().Contain(l => l.Radio == "6e");
    }

    [Fact]
    public void CreateMloClient_TotalRateIsSum()
    {
        // Arrange
        var mloClient = NetworkTestData.CreateMloClient();

        // Act
        var totalTxKbps = mloClient.MloLinks!.Sum(l => l.TxRateKbps ?? 0);
        var expectedTxMbps = totalTxKbps / 1000; // 574 + 2400 + 5760 = 8734 Mbps

        // Assert
        expectedTxMbps.Should().Be(8734);
    }

    [Fact]
    public void CreateBasicTopology_HasAllDevices()
    {
        // Arrange & Act
        var topology = NetworkTestData.CreateBasicTopology();

        // Assert
        topology.Devices.Should().HaveCount(4); // Gateway, Switch, Wired AP, Mesh AP
        topology.Clients.Should().HaveCount(2); // Wired client, Wireless client
        topology.Networks.Should().HaveCount(1); // Default network
    }

    #endregion

    #region VPN Hop Tests

    [Fact]
    public void TailscaleHop_HasWanPortNames()
    {
        // Arrange - Tailscale hop should have WAN port names for bottleneck description
        var hop = new NetworkHop
        {
            Type = HopType.Tailscale,
            DeviceName = "Tailscale",
            DeviceIp = "100.97.85.114",
            IngressSpeedMbps = 100,
            EgressSpeedMbps = 100,
            IngressPortName = "WAN",
            EgressPortName = "WAN"
        };

        // Assert
        hop.IngressPortName.Should().Be("WAN");
        hop.EgressPortName.Should().Be("WAN");
    }

    [Fact]
    public void TeleportHop_HasWanPortNames()
    {
        // Arrange - Teleport hop should have WAN port names for bottleneck description
        var hop = new NetworkHop
        {
            Type = HopType.Teleport,
            DeviceName = "Teleport",
            DeviceIp = "192.168.50.100",
            IngressSpeedMbps = 100,
            EgressSpeedMbps = 100,
            IngressPortName = "WAN",
            EgressPortName = "WAN"
        };

        // Assert
        hop.IngressPortName.Should().Be("WAN");
        hop.EgressPortName.Should().Be("WAN");
    }

    [Fact]
    public void WanHop_HasWanPortNames()
    {
        // Arrange - WAN hop for external IPs should have WAN port names
        var hop = new NetworkHop
        {
            Type = HopType.Wan,
            DeviceName = "WAN",
            DeviceIp = "8.8.8.8",
            IngressSpeedMbps = 100,
            EgressSpeedMbps = 100,
            IngressPortName = "WAN",
            EgressPortName = "WAN"
        };

        // Assert
        hop.IngressPortName.Should().Be("WAN");
        hop.EgressPortName.Should().Be("WAN");
    }

    [Fact]
    public void VpnPath_BottleneckDescription_ShowsWan()
    {
        // Arrange - Path with VPN hop as bottleneck should show "WAN" not "unknown"
        var path = new NetworkPath
        {
            TheoreticalMaxMbps = 29,
            RealisticMaxMbps = 29,
            HasRealBottleneck = true,
            BottleneckDescription = "29 Mbps link at Tailscale (WAN)",
            Hops = new List<NetworkHop>
            {
                new()
                {
                    Type = HopType.Tailscale,
                    DeviceName = "Tailscale",
                    IngressSpeedMbps = 29,
                    EgressSpeedMbps = 29,
                    IngressPortName = "WAN",
                    EgressPortName = "WAN",
                    IsBottleneck = true
                },
                new()
                {
                    Type = HopType.Gateway,
                    DeviceName = "Gateway",
                    IngressSpeedMbps = 1000,
                    EgressSpeedMbps = 1000
                },
                new()
                {
                    Type = HopType.Server,
                    DeviceName = "Server"
                }
            }
        };

        // Assert - Bottleneck description should show "WAN" not "unknown"
        path.BottleneckDescription.Should().Contain("WAN");
        path.BottleneckDescription.Should().NotContain("unknown");
    }

    [Fact]
    public void ExternalPath_IsExternalPath_IsTrue()
    {
        // Arrange - Path from external IP should have IsExternalPath = true
        var path = new NetworkPath
        {
            IsExternalPath = true,
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Tailscale, DeviceName = "Tailscale" },
                new() { Type = HopType.Gateway, DeviceName = "Gateway" },
                new() { Type = HopType.Server, DeviceName = "Server" }
            }
        };

        // Assert
        path.IsExternalPath.Should().BeTrue();
    }

    [Fact]
    public void GenerateInsights_ExternalPath_DoesNotShowGatewayWarning()
    {
        // Arrange - External path targeting gateway should NOT show gateway CPU warning
        var path = new NetworkPath
        {
            IsExternalPath = true,
            TargetIsGateway = true,
            TheoreticalMaxMbps = 1000,
            RealisticMaxMbps = 940,
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.Teleport, DeviceName = "Teleport" },
                new() { Type = HopType.Gateway, DeviceName = "Gateway" },
                new() { Type = HopType.Server, DeviceName = "Server" }
            }
        };
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert - Should NOT show gateway warning for external paths
        result.Insights.Should().NotContain("Gateway speed test - results limited by gateway CPU, not network");
    }

    #endregion
}
