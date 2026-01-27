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

    #region PoE Compatibility Tests

    [Fact]
    public void Analyze_ProfileForcesPoEOff_ExcludesPortsWithPoEEnabled()
    {
        // Arrange - profile turns off PoE, some ports have PoE enabled
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "Trunk No PoE",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "off", // Forces PoE off
            Autoneg = true,
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = true, PoeEnable = false, Speed = 1000, Autoneg = true
                },
                // Port 2: matching config, PoE enabled - should be EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                },
                // Port 3: matching config, PoE disabled - should be INCLUDED
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - only port 3 should be suggested (port 2 excluded due to PoE)
        result.Should().HaveCount(1);
        var suggestion = result[0];
        suggestion.PortsWithoutProfile.Should().Be(1);
        suggestion.AffectedPorts.Should().Contain(p => p.PortIndex == 3);
        suggestion.AffectedPorts.Should().NotContain(p => p.PortIndex == 2);
    }

    [Fact]
    public void Analyze_ProfileForcesPoEOff_IncludesSfpPortsWithoutPoECapability()
    {
        // Arrange - SFP ports don't have PoE capability, so profile's PoE setting is irrelevant
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "Trunk No PoE",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "off",
            Autoneg = true,
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = true, Media = "SFP+"
                },
                // Port 9: SFP port, no PoE capability - should be INCLUDED
                new SwitchPort
                {
                    PortIdx = 9, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = true, Media = "SFP+"
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - SFP port should be included
        result.Should().HaveCount(1);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 9);
    }

    #endregion

    #region Speed/Autoneg Compatibility Tests

    [Fact]
    public void Analyze_ProfileForcesSpeed_IncludesPortsWithMatchingSpeed()
    {
        // Arrange - profile forces 10G, ports at 10G should match regardless of their autoneg setting
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "10G Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "auto",
            Autoneg = false, // Forces speed
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile at 10G
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                },
                // Port 2: 10G with autoneg=true - should be INCLUDED (speed matches)
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = true
                },
                // Port 3: 10G with autoneg=false - should be INCLUDED
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - both ports 2 and 3 should be included
        result.Should().HaveCount(1);
        result[0].PortsWithoutProfile.Should().Be(2);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 2);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 3);
    }

    [Fact]
    public void Analyze_ProfileForcesSpeed_ExcludesPortsWithDifferentSpeed()
    {
        // Arrange - profile forces 10G, 2.5G ports should be excluded
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "10G Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "auto",
            Autoneg = false, // Forces speed
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile at 10G
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                },
                // Port 2: 2.5G - should be EXCLUDED (speed mismatch)
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 2500, Autoneg = true
                },
                // Port 3: 10G - should be INCLUDED
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - only port 3 should be included
        result.Should().HaveCount(1);
        result[0].PortsWithoutProfile.Should().Be(1);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 3);
        result[0].AffectedPorts.Should().NotContain(p => p.PortIndex == 2);
    }

    [Fact]
    public void Analyze_ProfileUsesAutoneg_ExcludesPortsWithForcedSpeed()
    {
        // Arrange - profile uses autoneg, ports with forced speed should be excluded
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "Autoneg Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "auto",
            Autoneg = true, // Uses autoneg
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile with autoneg
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, PoeEnable = false, Speed = 1000, Autoneg = true
                },
                // Port 2: autoneg=false (forced speed) - should be EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                },
                // Port 3: autoneg=true - should be INCLUDED
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - only port 3 should be included
        result.Should().HaveCount(1);
        result[0].PortsWithoutProfile.Should().Be(1);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 3);
        result[0].AffectedPorts.Should().NotContain(p => p.PortIndex == 2);
    }

    [Fact]
    public void Analyze_ProfileForcesSpeedNoExistingPorts_SkipsSuggestion()
    {
        // Arrange - profile forces speed but no ports currently use it - too risky
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "10G Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "auto",
            Autoneg = false, // Forces speed
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // No ports currently use the profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                },
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - no suggestion because profile forces speed and no ports use it
        result.Should().BeEmpty();
    }

    #endregion

    #region Suggestion Type Tests

    [Fact]
    public void Analyze_SomePortsUseProfile_ReturnsExtendUsageSuggestion()
    {
        // Arrange
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "Standard Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "auto",
            Autoneg = true,
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port already using profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, Speed = 1000, Autoneg = true
                },
                // Port without profile
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.ExtendUsage);
        result[0].PortsAlreadyUsingProfile.Should().Be(1);
        result[0].PortsWithoutProfile.Should().Be(1);
    }

    [Fact]
    public void Analyze_NoPortsUseMatchingProfile_ReturnsApplyExistingSuggestion()
    {
        // Arrange - profile exists but no ports use it yet
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "Standard Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "auto",
            Autoneg = true,
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                },
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.ApplyExisting);
        result[0].MatchingProfileName.Should().Be("Standard Trunk");
    }

    [Fact]
    public void Analyze_TwoOrMoreMatchingPortsNoProfile_ReturnsCreateNewSuggestion()
    {
        // Arrange - 2+ ports with same config, no matching profile
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                },
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act - no profiles provided
        var result = _analyzer.Analyze(
            new[] { device },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - 2 ports is enough for CreateNew suggestion
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].SuggestedProfileName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Analyze_SingleMatchingPortNoProfile_ReturnsEmptyList()
    {
        // Arrange - only 1 port, need 2+ for CreateNew suggestion
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act - no profiles provided
        var result = _analyzer.Analyze(
            new[] { device },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - need 2+ ports for CreateNew
        result.Should().BeEmpty();
    }

    #endregion

    #region Combined Filter Tests

    [Fact]
    public void Analyze_ProfileForcesPoEOffAndSpeed_AppliesBothFilters()
    {
        // Arrange - profile forces both PoE off and specific speed
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "10G No PoE Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "off",
            Autoneg = false, // Forces speed
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = false
                },
                // Port 2: PoE enabled - EXCLUDED by PoE
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = false
                },
                // Port 3: wrong speed - EXCLUDED by speed
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 2500, Autoneg = true
                },
                // Port 4: correct config - INCLUDED
                new SwitchPort
                {
                    PortIdx = 4, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 10000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - only port 4 should be included
        result.Should().HaveCount(1);
        result[0].PortsWithoutProfile.Should().Be(1);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 4);
        result[0].AffectedPorts.Should().NotContain(p => p.PortIndex == 2);
        result[0].AffectedPorts.Should().NotContain(p => p.PortIndex == 3);
    }

    [Fact]
    public void Analyze_AllCandidatesFilteredOut_ReturnsNoSuggestion()
    {
        // Arrange - all candidates are filtered by PoE
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" }
        };

        var profile = new UniFiPortProfile
        {
            Id = "profile-1",
            Name = "No PoE Trunk",
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            PoeMode = "off",
            Autoneg = true,
            ExcludedNetworkConfIds = new List<string>()
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                // Port 1: already uses profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, PoeEnable = false, Speed = 1000, Autoneg = true
                },
                // Port 2: PoE enabled - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                },
                // Port 3: PoE enabled - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - no suggestion because all candidates filtered
        result.Should().BeEmpty();
    }

    #endregion

    #region WAN/VPN Network Exclusion Tests

    [Fact]
    public void Analyze_ExcludesWanAndVpnNetworks()
    {
        // Arrange - WAN and VPN networks should not be considered for port profiles
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10, Purpose = "corporate" },
            new UniFiNetworkConfig { Id = "net-wan", Name = "WAN", Purpose = "wan" },
            new UniFiNetworkConfig { Id = "net-vpn", Name = "VPN", Purpose = "site-vpn" }
        };

        var device = new UniFiDeviceResponse
        {
            Id = "switch1",
            Mac = "aa:bb:cc:00:00:01",
            Name = "Switch 1",
            Type = "usw",
            PortTable = new List<SwitchPort>
            {
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                },
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                },
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new List<UniFiPortProfile>(),
            networks);

        // Assert - should work, WAN/VPN networks filtered internally
        result.Should().HaveCount(1);
    }

    #endregion
}
