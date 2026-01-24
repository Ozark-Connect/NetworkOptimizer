using FluentAssertions;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Services;

/// <summary>
/// Tests for FirewallZoneLookup service.
/// </summary>
public class FirewallZoneLookupTests
{
    private static List<UniFiFirewallZone> CreateStandardZones() =>
    [
        new UniFiFirewallZone { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
        new UniFiFirewallZone { Id = "zone-external-002", ZoneKey = "external", Name = "External" },
        new UniFiFirewallZone { Id = "zone-dmz-003", ZoneKey = "dmz", Name = "DMZ" },
        new UniFiFirewallZone { Id = "zone-hotspot-004", ZoneKey = "hotspot", Name = "Hotspot" },
        new UniFiFirewallZone { Id = "zone-vpn-005", ZoneKey = "vpn", Name = "VPN" },
        new UniFiFirewallZone { Id = "zone-gateway-006", ZoneKey = "gateway", Name = "Gateway" }
    ];

    #region Constructor Tests

    [Fact]
    public void Constructor_WithZones_LoadsAllZones()
    {
        // Arrange
        var zones = CreateStandardZones();

        // Act
        var lookup = new FirewallZoneLookup(zones);

        // Assert
        lookup.HasZoneData.Should().BeTrue();
        lookup.ZonesById.Should().HaveCount(6);
        lookup.ZonesByKey.Should().HaveCount(6);
    }

    [Fact]
    public void Constructor_WithNullZones_HasNoData()
    {
        // Act
        var lookup = new FirewallZoneLookup(null);

        // Assert
        lookup.HasZoneData.Should().BeFalse();
        lookup.ZonesById.Should().BeEmpty();
        lookup.ZonesByKey.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithEmptyZones_HasNoData()
    {
        // Act
        var lookup = new FirewallZoneLookup([]);

        // Assert
        lookup.HasZoneData.Should().BeFalse();
        lookup.ZonesById.Should().BeEmpty();
        lookup.ZonesByKey.Should().BeEmpty();
    }

    #endregion

    #region GetZoneById Tests

    [Fact]
    public void GetZoneById_ExistingId_ReturnsZone()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetZoneById("zone-dmz-003");

        // Assert
        result.Should().NotBeNull();
        result!.ZoneKey.Should().Be("dmz");
        result.Name.Should().Be("DMZ");
    }

    [Fact]
    public void GetZoneById_NonExistingId_ReturnsNull()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetZoneById("nonexistent-zone-id");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetZoneById_NullId_ReturnsNull()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetZoneById(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetZoneById_CaseInsensitive()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetZoneById("ZONE-DMZ-003");

        // Assert
        result.Should().NotBeNull();
        result!.ZoneKey.Should().Be("dmz");
    }

    #endregion

    #region GetZoneByKey Tests

    [Fact]
    public void GetZoneByKey_ExistingKey_ReturnsZone()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetZoneByKey("hotspot");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("zone-hotspot-004");
        result.Name.Should().Be("Hotspot");
    }

    [Fact]
    public void GetZoneByKey_CaseInsensitive()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetZoneByKey("HOTSPOT");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("zone-hotspot-004");
    }

    #endregion

    #region IsDmzZone Tests

    [Fact]
    public void IsDmzZone_DmzZoneId_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsDmzZone("zone-dmz-003");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsDmzZone_NonDmzZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsDmzZone("zone-internal-001");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsDmzZone_NullZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsDmzZone(null);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsDmzZone_NonExistentZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsDmzZone("nonexistent-id");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region IsHotspotZone Tests

    [Fact]
    public void IsHotspotZone_HotspotZoneId_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsHotspotZone("zone-hotspot-004");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsHotspotZone_NonHotspotZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsHotspotZone("zone-internal-001");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsHotspotZone_NullZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsHotspotZone(null);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region IsExternalZone Tests

    [Fact]
    public void IsExternalZone_ExternalZoneId_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsExternalZone("zone-external-002");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsExternalZone_NonExternalZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsExternalZone("zone-internal-001");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region IsInternalZone Tests

    [Fact]
    public void IsInternalZone_InternalZoneId_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsInternalZone("zone-internal-001");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsInternalZone_NonInternalZoneId_ReturnsFalse()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.IsInternalZone("zone-dmz-003");

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region GetZoneId Helper Tests

    [Fact]
    public void GetExternalZoneId_ReturnsCorrectId()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetExternalZoneId();

        // Assert
        result.Should().Be("zone-external-002");
    }

    [Fact]
    public void GetDmzZoneId_ReturnsCorrectId()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetDmzZoneId();

        // Assert
        result.Should().Be("zone-dmz-003");
    }

    [Fact]
    public void GetHotspotZoneId_ReturnsCorrectId()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.GetHotspotZoneId();

        // Assert
        result.Should().Be("zone-hotspot-004");
    }

    [Fact]
    public void GetExternalZoneId_NoZoneData_ReturnsNull()
    {
        // Arrange
        var lookup = new FirewallZoneLookup(null);

        // Act
        var result = lookup.GetExternalZoneId();

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region ValidateWanZoneAssumption Tests

    [Fact]
    public void ValidateWanZoneAssumption_CorrectExternalZone_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateWanZoneAssumption("WAN", "zone-external-002");

        // Assert
        result.Should().BeTrue();
        lookup.ValidationWarnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateWanZoneAssumption_WrongZone_ReturnsFalseWithWarning()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateWanZoneAssumption("WAN", "zone-internal-001");

        // Assert
        result.Should().BeFalse();
        lookup.ValidationWarnings.Should().ContainSingle()
            .Which.Should().Contain("Internal").And.Contain("expected zone_key 'external'");
    }

    [Fact]
    public void ValidateWanZoneAssumption_UnknownZoneId_ReturnsFalseWithWarning()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateWanZoneAssumption("WAN", "unknown-zone-id");

        // Assert
        result.Should().BeFalse();
        lookup.ValidationWarnings.Should().ContainSingle()
            .Which.Should().Contain("not found in the zone lookup");
    }

    [Fact]
    public void ValidateWanZoneAssumption_NoZoneData_ReturnsTrue()
    {
        // Arrange
        var lookup = new FirewallZoneLookup(null);

        // Act
        var result = lookup.ValidateWanZoneAssumption("WAN", "any-zone-id");

        // Assert
        result.Should().BeTrue(); // Can't validate without data, returns true
        lookup.ValidationWarnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateWanZoneAssumption_NullZoneId_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateWanZoneAssumption("WAN", null);

        // Assert
        result.Should().BeTrue(); // Can't validate without zone ID
    }

    #endregion

    #region ValidateExternalZoneId Tests

    [Fact]
    public void ValidateExternalZoneId_MatchesLookup_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateExternalZoneId("zone-external-002");

        // Assert
        result.Should().BeTrue();
        lookup.ValidationWarnings.Should().BeEmpty();
    }

    [Fact]
    public void ValidateExternalZoneId_DoesNotMatchLookup_ReturnsFalseWithWarning()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateExternalZoneId("wrong-external-id");

        // Assert
        result.Should().BeFalse();
        lookup.ValidationWarnings.Should().ContainSingle()
            .Which.Should().Contain("does not match zone lookup external zone ID");
    }

    [Fact]
    public void ValidateExternalZoneId_NoExternalZoneInLookup_ReturnsFalseWithWarning()
    {
        // Arrange - zones without external
        var zones = new List<UniFiFirewallZone>
        {
            new UniFiFirewallZone { Id = "zone-internal-001", ZoneKey = "internal", Name = "Internal" },
            new UniFiFirewallZone { Id = "zone-dmz-003", ZoneKey = "dmz", Name = "DMZ" }
        };
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateExternalZoneId("some-external-id");

        // Assert
        result.Should().BeFalse();
        lookup.ValidationWarnings.Should().ContainSingle()
            .Which.Should().Contain("does not contain an 'external' zone");
    }

    [Fact]
    public void ValidateExternalZoneId_NoZoneData_ReturnsTrue()
    {
        // Arrange
        var lookup = new FirewallZoneLookup(null);

        // Act
        var result = lookup.ValidateExternalZoneId("any-zone-id");

        // Assert
        result.Should().BeTrue(); // Can't validate without data
    }

    [Fact]
    public void ValidateExternalZoneId_NullDeterminedId_ReturnsTrue()
    {
        // Arrange
        var zones = CreateStandardZones();
        var lookup = new FirewallZoneLookup(zones);

        // Act
        var result = lookup.ValidateExternalZoneId(null);

        // Assert
        result.Should().BeTrue(); // Can't validate without ID
    }

    #endregion
}
