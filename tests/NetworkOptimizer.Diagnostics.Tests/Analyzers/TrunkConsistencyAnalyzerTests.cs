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

    #region Trunk Link Discovery Tests

    [Fact]
    public void Analyze_TwoConnectedSwitches_BothTrunks_NoMismatch_ReturnsEmpty()
    {
        // Arrange - two switches connected, both allowing same VLANs
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20, Purpose = "corporate" }
        };

        var switch1 = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Core Switch",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string>(), IsUplink = true
                }
            }
        };

        var switch2 = new UniFiDeviceResponse
        {
            Id = "switch2",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Access Switch",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 24 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string>(), IsUplink = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { switch1, switch2 },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - no mismatch since both allow all VLANs
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_TwoConnectedSwitches_VlanMismatch_ReturnsIssue()
    {
        // Arrange - switch2 excludes VLAN 20 that switch1 allows
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20, Purpose = "corporate" }
        };

        var switch1 = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Core Switch",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string>(), IsUplink = false
                }
            }
        };

        var switch2 = new UniFiDeviceResponse
        {
            Id = "switch2",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Access Switch",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 24 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string> { "net-2" }, // Excludes VLAN 20
                    IsUplink = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { switch1, switch2 },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - should detect VLAN 20 mismatch
        result.Should().HaveCount(1);
        result[0].Mismatches.Should().HaveCount(1);
        result[0].Mismatches[0].NetworkName.Should().Be("VLAN 20");
    }

    [Fact]
    public void Analyze_AccessPortsNotTrunks_NoIssues()
    {
        // Arrange - two switches connected but using access ports (not trunks)
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var switch1 = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Core Switch",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 24, Forward = "native", // Access port, not trunk
                    NativeNetworkConfId = "net-1"
                }
            }
        };

        var switch2 = new UniFiDeviceResponse
        {
            Id = "switch2",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Access Switch",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 24 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "native", // Access port, not trunk
                    NativeNetworkConfId = "net-1", IsUplink = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { switch1, switch2 },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - access ports are not analyzed for trunk consistency
        result.Should().BeEmpty();
    }

    #endregion

    #region Confidence Level Tests

    [Fact]
    public void Analyze_VlanOnMostTrunks_HighConfidence()
    {
        // Arrange - VLAN present on most trunks, one side missing it = high confidence issue
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var coreSwitch = new UniFiDeviceResponse
        {
            Id = "core",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Core Switch",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort { PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom", ExcludedNetworkConfIds = new List<string>() },
                new SwitchPort { PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom", ExcludedNetworkConfIds = new List<string>() },
                new SwitchPort { PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom", ExcludedNetworkConfIds = new List<string>() }
            }
        };

        var accessSwitch1 = new UniFiDeviceResponse
        {
            Id = "access1",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Access Switch 1",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 1 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort { PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom", ExcludedNetworkConfIds = new List<string>(), IsUplink = true }
            }
        };

        var accessSwitch2 = new UniFiDeviceResponse
        {
            Id = "access2",
            Mac = "aa:bb:cc:00:00:03",
            Name = "Access Switch 2",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 2 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort { PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom", ExcludedNetworkConfIds = new List<string>(), IsUplink = true }
            }
        };

        var accessSwitch3 = new UniFiDeviceResponse
        {
            Id = "access3",
            Mac = "aa:bb:cc:00:00:04",
            Name = "Access Switch 3",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 3 },
            PortTable = new List<SwitchPort>
            {
                // This switch excludes the VLAN - should be high confidence issue
                new SwitchPort { PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom", ExcludedNetworkConfIds = new List<string> { "net-1" }, IsUplink = true }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { coreSwitch, accessSwitch1, accessSwitch2, accessSwitch3 },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - should find high confidence issue for switch 3
        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(Models.DiagnosticConfidence.High);
    }

    #endregion

    #region Port Profile Tests

    [Fact]
    public void Analyze_PortWithProfile_UsesProfileVlans()
    {
        // Arrange - port uses a profile that excludes VLANs
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "Limited Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            ExcludedNetworkConfIds = new List<string> { "net-2" } // Profile excludes VLAN 20
        };

        var switch1 = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Core Switch",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string>() // Core allows all
                }
            }
        };

        var switch2 = new UniFiDeviceResponse
        {
            Id = "switch2",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Access Switch",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 24 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, PortConfId = "profile-1", // Uses the limiting profile
                    Forward = "customize", TaggedVlanMgmt = "custom", IsUplink = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { switch1, switch2 },
            new[] { profile },
            networks);

        // Assert - should detect VLAN 20 mismatch due to profile
        result.Should().HaveCount(1);
        result[0].Mismatches.Should().Contain(m => m.NetworkName == "VLAN 20");
    }

    #endregion

    #region Recommendation Tests

    [Fact]
    public void Analyze_MismatchedVlans_GeneratesRecommendation()
    {
        // Arrange
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20, Purpose = "corporate" }
        };

        var switch1 = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Core Switch",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 24, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string>()
                }
            }
        };

        var switch2 = new UniFiDeviceResponse
        {
            Id = "switch2",
            Mac = "aa:bb:cc:00:00:02",
            Name = "Access Switch",
            Type = "usw",
            Uplink = new UplinkInfo { UplinkMac = "aa:bb:cc:00:00:01", UplinkRemotePort = 24 },
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    ExcludedNetworkConfIds = new List<string> { "net-2" }, IsUplink = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { switch1, switch2 },
            new List<UniFiPortProfile>(),
            networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().NotBeNullOrEmpty();
        result[0].Recommendation.Should().Contain("VLAN 20");
        result[0].Recommendation.Should().Contain("Access Switch");
    }

    #endregion
}
