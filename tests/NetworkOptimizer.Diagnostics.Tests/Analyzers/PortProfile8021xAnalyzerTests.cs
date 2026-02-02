using FluentAssertions;
using NetworkOptimizer.Diagnostics.Analyzers;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Diagnostics.Tests.Analyzers;

public class PortProfile8021xAnalyzerTests
{
    private readonly PortProfile8021xAnalyzer _analyzer;

    public PortProfile8021xAnalyzerTests()
    {
        _analyzer = new PortProfile8021xAnalyzer();
    }

    #region Empty/Null Input Tests

    [Fact]
    public void Analyze_EmptyProfiles_ReturnsEmptyList()
    {
        // Arrange
        var profiles = new List<UniFiPortProfile>();
        var networks = CreateSampleNetworks(5);

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_EmptyNetworks_ReturnsEmptyList()
    {
        // Arrange
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk All", null, "auto")
        };
        var networks = new List<UniFiNetworkConfig>();

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region Non-Trunk Profile Tests

    [Fact]
    public void Analyze_AccessProfile_ReturnsNoIssues()
    {
        // Arrange - access port profile (Forward != "customize")
        var profiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-1",
                Name = "Access Port",
                Forward = "native",
                TaggedVlanMgmt = "block_all",
                Dot1xCtrl = "auto"
            }
        };
        var networks = CreateSampleNetworks(5);

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_DisabledProfile_ReturnsNoIssues()
    {
        // Arrange - disabled port profile
        var profiles = new List<UniFiPortProfile>
        {
            new UniFiPortProfile
            {
                Id = "profile-1",
                Name = "Disabled Port",
                Forward = "disabled",
                TaggedVlanMgmt = "block_all",
                Dot1xCtrl = "auto"
            }
        };
        var networks = CreateSampleNetworks(5);

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region VLAN Count Threshold Tests

    [Fact]
    public void Analyze_TrunkProfileWithOneVlan_ReturnsNoIssues()
    {
        // Arrange - only 1 VLAN (not trunk-like)
        var networks = CreateSampleNetworks(5);
        var excludeAllButOne = networks.Skip(1).Select(n => n.Id).ToList();

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Single VLAN", excludeAllButOne, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert - 1 VLAN is below threshold of >2
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_TrunkProfileWithTwoVlans_ReturnsNoIssues()
    {
        // Arrange - exactly 2 VLANs (at threshold, not above)
        var networks = CreateSampleNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Two VLANs", excludeAllButTwo, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert - 2 VLANs is not above threshold of >2
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_TrunkProfileWithThreeVlans_ReturnsIssue()
    {
        // Arrange - 3 VLANs (above threshold of >2)
        var networks = CreateSampleNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Three VLANs", excludeAllButThree, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].ProfileName.Should().Be("Three VLANs");
        result[0].TaggedVlanCount.Should().Be(3);
        result[0].AllowsAllVlans.Should().BeFalse();
    }

    #endregion

    #region Allow All VLAN Tests

    [Fact]
    public void Analyze_AllowAllVlans_NullExcludedList_ReturnsIssue()
    {
        // Arrange - null excluded list means "Allow All"
        var networks = CreateSampleNetworks(5);

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Allow All (null)", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].ProfileName.Should().Be("Allow All (null)");
        result[0].AllowsAllVlans.Should().BeTrue();
        result[0].TaggedVlanCount.Should().Be(5); // All 5 networks
    }

    [Fact]
    public void Analyze_AllowAllVlans_EmptyExcludedList_ReturnsIssue()
    {
        // Arrange - empty excluded list means "Allow All"
        var networks = CreateSampleNetworks(5);

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Allow All (empty)", new List<string>(), "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].ProfileName.Should().Be("Allow All (empty)");
        result[0].AllowsAllVlans.Should().BeTrue();
        result[0].TaggedVlanCount.Should().Be(5);
    }

    #endregion

    #region 802.1X Control Setting Tests

    [Fact]
    public void Analyze_Dot1xCtrlAuto_ReturnsIssue()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Auto", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].CurrentDot1xCtrl.Should().Be("auto");
    }

    [Fact]
    public void Analyze_Dot1xCtrlAutoUppercase_ReturnsIssue()
    {
        // Arrange - case insensitive check
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk AUTO", null, "AUTO")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
    }

    [Fact]
    public void Analyze_Dot1xCtrlNull_ReturnsIssue()
    {
        // Arrange - null defaults to "auto"
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Null Dot1x", null, null)
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].CurrentDot1xCtrl.Should().Be("auto");
    }

    [Fact]
    public void Analyze_Dot1xCtrlForceAuthorized_ReturnsNoIssues()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Force Auth", null, "force_authorized")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_Dot1xCtrlForceUnauthorized_ReturnsNoIssues()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Force Unauth", null, "force_unauthorized")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region Multiple Profiles Tests

    [Fact]
    public void Analyze_MixedProfiles_ReturnsOnlyIssues()
    {
        // Arrange - mix of profiles: some with issues, some without
        var networks = CreateSampleNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();

        var profiles = new List<UniFiPortProfile>
        {
            // Issue: Allow All + Auto
            CreateTrunkProfile("profile-1", "Trunk All Auto", null, "auto"),
            // No issue: Allow All + Force Authorized
            CreateTrunkProfile("profile-2", "Trunk All ForceAuth", null, "force_authorized"),
            // No issue: 2 VLANs (below threshold)
            CreateTrunkProfile("profile-3", "Trunk Few VLANs", excludeAllButTwo, "auto"),
            // No issue: Access port
            new UniFiPortProfile
            {
                Id = "profile-4",
                Name = "Access Port",
                Forward = "native",
                TaggedVlanMgmt = "block_all",
                Dot1xCtrl = "auto"
            },
            // Issue: 3 VLANs + Auto
            CreateTrunkProfile("profile-5", "Trunk Some VLANs", networks.Skip(3).Select(n => n.Id).ToList(), "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(2);
        result.Select(r => r.ProfileName).Should().Contain("Trunk All Auto");
        result.Select(r => r.ProfileName).Should().Contain("Trunk Some VLANs");
    }

    #endregion

    #region Recommendation Text Tests

    [Fact]
    public void Analyze_RecommendationText_IncludesProfileName()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "My Trunk Profile", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().Contain("My Trunk Profile");
    }

    [Fact]
    public void Analyze_RecommendationText_MentionsForceAuthorized()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Profile", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().Contain("Force Authorized");
    }

    [Fact]
    public void Analyze_RecommendationText_AllowAllVlans_SaysAllVlans()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Profile", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().Contain("all VLANs");
    }

    [Fact]
    public void Analyze_RecommendationText_SpecificVlans_SaysVlanCount()
    {
        // Arrange
        var networks = CreateSampleNetworks(5);
        var excludeTwo = networks.Take(2).Select(n => n.Id).ToList();

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Profile", excludeTwo, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].Recommendation.Should().Contain("3 VLANs"); // 5 - 2 excluded = 3
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Analyze_NetworksWithZeroVlan_AreExcluded()
    {
        // Arrange - networks with VLAN 0 should not count as VLAN networks
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-0", Name = "Default", Vlan = 0 },
            new UniFiNetworkConfig { Id = "net-1", Name = "VLAN 10", Vlan = 10 },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 20", Vlan = 20 },
            new UniFiNetworkConfig { Id = "net-3", Name = "VLAN 30", Vlan = 30 }
        };

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk All", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].TaggedVlanCount.Should().Be(3); // Only VLAN 10, 20, 30 counted
    }

    [Fact]
    public void Analyze_NetworksWithNullVlan_AreExcluded()
    {
        // Arrange
        var networks = new List<UniFiNetworkConfig>
        {
            new UniFiNetworkConfig { Id = "net-1", Name = "No VLAN", Vlan = null },
            new UniFiNetworkConfig { Id = "net-2", Name = "VLAN 10", Vlan = 10 },
            new UniFiNetworkConfig { Id = "net-3", Name = "VLAN 20", Vlan = 20 },
            new UniFiNetworkConfig { Id = "net-4", Name = "VLAN 30", Vlan = 30 }
        };

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk All", null, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert
        result.Should().HaveCount(1);
        result[0].TaggedVlanCount.Should().Be(3); // Only networks with VLAN > 0
    }

    [Fact]
    public void Analyze_ExcludedNetworkNotInList_IgnoresUnknownNetworkIds()
    {
        // Arrange - excluded list contains IDs not in networks list
        var networks = CreateSampleNetworks(5);

        var excludeWithUnknown = new List<string>
        {
            "net-0", // valid
            "unknown-network-id", // invalid - should be ignored
            "another-unknown" // invalid
        };

        var profiles = new List<UniFiPortProfile>
        {
            CreateTrunkProfile("profile-1", "Trunk Profile", excludeWithUnknown, "auto")
        };

        // Act
        var result = _analyzer.Analyze(profiles, networks);

        // Assert - 5 networks - 1 valid excluded = 4 VLANs
        result.Should().HaveCount(1);
        result[0].TaggedVlanCount.Should().Be(4);
    }

    #endregion

    #region Helper Methods

    private static List<UniFiNetworkConfig> CreateSampleNetworks(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new UniFiNetworkConfig
            {
                Id = $"net-{i}",
                Name = $"VLAN {(i + 1) * 10}",
                Vlan = (i + 1) * 10,
                Purpose = "corporate"
            })
            .ToList();
    }

    private static UniFiPortProfile CreateTrunkProfile(
        string id,
        string name,
        List<string>? excludedNetworkIds,
        string? dot1xCtrl)
    {
        return new UniFiPortProfile
        {
            Id = id,
            Name = name,
            Forward = "customize",
            TaggedVlanMgmt = "custom",
            ExcludedNetworkConfIds = excludedNetworkIds,
            Dot1xCtrl = dot1xCtrl
        };
    }

    #endregion
}
