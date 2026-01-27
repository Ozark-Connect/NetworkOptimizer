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
    public void Analyze_ProfileForcesSpeedNoExistingPorts_CreatesFallbackSuggestion()
    {
        // Arrange - profile forces speed but no ports currently use it
        // We don't suggest applying it (too risky), but we DO suggest creating a new profile for these ports
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

        // Assert - creates fallback CreateNew suggestion for excluded ports
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].AffectedPorts.Should().HaveCount(2);
        result[0].SuggestedProfileName.Should().Contain("(PoE)");
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
        // Some ports excluded by PoE, some by speed - but excluded ports have different speeds
        // so no fallback suggestion for them
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
                // Port 2: PoE enabled - EXCLUDED by PoE (10G, autoneg=false)
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = false
                },
                // Port 3: wrong speed - EXCLUDED by speed (2.5G, autoneg=true)
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

        // Assert - only ExtendUsage for port 4
        // No fallback for ports 2+3 because they have different speeds/autoneg settings
        result.Should().HaveCount(1);

        result[0].Type.Should().Be(Models.PortProfileSuggestionType.ExtendUsage);
        result[0].PortsWithoutProfile.Should().Be(1);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 4);
        result[0].AffectedPorts.Should().NotContain(p => p.PortIndex == 2);
        result[0].AffectedPorts.Should().NotContain(p => p.PortIndex == 3);
    }

    [Fact]
    public void Analyze_AllCandidatesFilteredOut_CreatesFallbackSuggestion()
    {
        // Arrange - all candidates are filtered by PoE, but since there are 2+,
        // we create a fallback CreateNew suggestion for those excluded ports
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
                // Port 2: PoE enabled - EXCLUDED from existing profile
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                },
                // Port 3: PoE enabled - EXCLUDED from existing profile
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

        // Assert - creates fallback CreateNew suggestion for excluded ports (ports 2 and 3 with PoE)
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].AffectedPorts.Should().HaveCount(2);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 2);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 3);
        result[0].SuggestedProfileName.Should().Contain("(PoE)");
    }

    [Fact]
    public void Analyze_SomePortsCompatibleSomeExcluded_ReturnsBothSuggestions()
    {
        // Arrange - some ports can use existing profile, others excluded due to PoE
        // Should return TWO suggestions: ExtendUsage for compatible, CreateNew for excluded
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
                // Port 2: compatible - no PoE
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 1000, Autoneg = true
                },
                // Port 3: PoE enabled - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                },
                // Port 4: PoE enabled - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 4, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - TWO suggestions: ExtendUsage for port 2, CreateNew for ports 3+4
        result.Should().HaveCount(2);

        var extendSuggestion = result.First(s => s.Type == Models.PortProfileSuggestionType.ExtendUsage);
        extendSuggestion.PortsWithoutProfile.Should().Be(1);
        extendSuggestion.AffectedPorts.Should().Contain(p => p.PortIndex == 2);

        var createSuggestion = result.First(s => s.Type == Models.PortProfileSuggestionType.CreateNew);
        createSuggestion.PortsWithoutProfile.Should().Be(2);
        createSuggestion.AffectedPorts.Should().Contain(p => p.PortIndex == 3);
        createSuggestion.AffectedPorts.Should().Contain(p => p.PortIndex == 4);
        createSuggestion.SuggestedProfileName.Should().Contain("(PoE)");
    }

    [Fact]
    public void Analyze_SingleExcludedPort_NoFallbackSuggestion()
    {
        // Arrange - only 1 port excluded, need 2+ for fallback
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
                // Port 2: compatible - no PoE
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = false, PoeEnable = false, Speed = 1000, Autoneg = true
                },
                // Port 3: PoE enabled - EXCLUDED (only 1, not enough for fallback)
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

        // Assert - only 1 suggestion (ExtendUsage), no fallback for single excluded port
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.ExtendUsage);
        result[0].PortsWithoutProfile.Should().Be(1);
        result[0].AffectedPorts.Should().Contain(p => p.PortIndex == 2);
    }

    [Fact]
    public void Analyze_ExcludedPortsWithDifferentSpeeds_NoFallbackSuggestion()
    {
        // Arrange - excluded ports have different speeds (10G vs 2.5G)
        // They can't share a profile, so no fallback suggestion should be created
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
                // Port 2: PoE enabled, 10G forced - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = false
                },
                // Port 3: PoE enabled, 2.5G forced - EXCLUDED with different speed
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 2500, Autoneg = false
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - no fallback suggestion because excluded ports have incompatible speeds
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ExcludedPortsWithSameForcedSpeed_CreatesFallbackSuggestion()
    {
        // Arrange - excluded ports have same forced speed (both 10G)
        // They CAN share a profile, so fallback suggestion should be created
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
                // Port 2: PoE enabled, 10G - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = false
                },
                // Port 3: PoE enabled, 10G - EXCLUDED with same speed
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = false
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - fallback suggestion created because excluded ports have same speed
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].AffectedPorts.Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_ExcludedPortsDifferentSpeeds_NoFallbackSuggestion()
    {
        // Arrange - excluded ports have different speeds (1G vs 10G)
        // Even though both use autoneg, they're at different speeds so no fallback
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
                // Port 2: PoE enabled, 1G - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 1000, Autoneg = true
                },
                // Port 3: PoE enabled, 10G (different speed)
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = true
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - no fallback because ports have different speeds
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_ExcludedPortsMixedAutonegSameSpeed_CreatesFallbackSuggestion()
    {
        // Arrange - one excluded port uses autoneg, another forces speed
        // BUT they're both at the same speed, so they CAN share a profile
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
                // Port 2: PoE enabled, autoneg=true at 10G - EXCLUDED
                new SwitchPort
                {
                    PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = true
                },
                // Port 3: PoE enabled, autoneg=false at 10G - EXCLUDED but same speed
                new SwitchPort
                {
                    PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortPoe = true, PoeEnable = true, Speed = 10000, Autoneg = false
                }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new[] { profile },
            networks);

        // Assert - fallback created because both excluded ports are at same speed
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].AffectedPorts.Should().HaveCount(2);
    }

    #endregion

    #region Severity Level Tests

    [Fact]
    public void Analyze_FiveOrMorePortsForCreateNew_ReturnsSeverityRecommendation()
    {
        // Arrange - 5+ ports without profile = Recommendation severity
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
                new SwitchPort { PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 4, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 5, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true }
            }
        };

        // Act - no profiles, 5 ports = CreateNew with Recommendation severity
        var result = _analyzer.Analyze(
            new[] { device },
            new List<UniFiPortProfile>(),
            networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].Severity.Should().Be(Models.PortProfileSuggestionSeverity.Recommendation);
    }

    [Fact]
    public void Analyze_FourPortsForCreateNew_ReturnsSeverityInfo()
    {
        // Arrange - 4 ports (< 5) = Info severity
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
                new SwitchPort { PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 4, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true }
            }
        };

        // Act
        var result = _analyzer.Analyze(
            new[] { device },
            new List<UniFiPortProfile>(),
            networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Type.Should().Be(Models.PortProfileSuggestionType.CreateNew);
        result[0].Severity.Should().Be(Models.PortProfileSuggestionSeverity.Info);
    }

    [Fact]
    public void Analyze_ThreeOrMorePortsForExtendUsage_ReturnsSeverityRecommendation()
    {
        // Arrange - 3+ ports could extend existing profile = Recommendation severity
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
                // Port 1: already uses profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, Speed = 1000, Autoneg = true
                },
                // Ports 2-4: 3 ports without profile
                new SwitchPort { PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 3, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true },
                new SwitchPort { PortIdx = 4, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true }
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
        result[0].Severity.Should().Be(Models.PortProfileSuggestionSeverity.Recommendation);
    }

    [Fact]
    public void Analyze_TwoPortsForExtendUsage_ReturnsSeverityInfo()
    {
        // Arrange - 2 ports (< 3) = Info severity
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
                // Port 1: already uses profile
                new SwitchPort
                {
                    PortIdx = 1, Forward = "customize", TaggedVlanMgmt = "custom",
                    PortConfId = "profile-1", PortPoe = false, Speed = 1000, Autoneg = true
                },
                // Port 2: 1 port without profile
                new SwitchPort { PortIdx = 2, Forward = "customize", TaggedVlanMgmt = "custom", PortPoe = false, Speed = 1000, Autoneg = true }
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
        result[0].Severity.Should().Be(Models.PortProfileSuggestionSeverity.Info);
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
