using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for UniFiFirewallZone model and FirewallZoneKeys constants.
/// </summary>
public class UniFiFirewallZoneTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    #region JSON Deserialization Tests

    [Fact]
    public void Deserialize_FullZone_MapsAllProperties()
    {
        // Arrange - sample zone from UniFi API
        var json = """
        {
            "_id": "67890abcdef123456789",
            "name": "DMZ",
            "zone_key": "dmz",
            "network_ids": ["net-001", "net-002"],
            "default_zone": false,
            "attr_no_edit": false,
            "external_id": "ext-123",
            "site_id": "default"
        }
        """;

        // Act
        var zone = JsonSerializer.Deserialize<UniFiFirewallZone>(json, JsonOptions);

        // Assert
        zone.Should().NotBeNull();
        zone!.Id.Should().Be("67890abcdef123456789");
        zone.Name.Should().Be("DMZ");
        zone.ZoneKey.Should().Be("dmz");
        zone.NetworkIds.Should().HaveCount(2);
        zone.NetworkIds.Should().Contain("net-001");
        zone.NetworkIds.Should().Contain("net-002");
        zone.IsDefaultZone.Should().BeFalse();
        zone.IsReadOnly.Should().BeFalse();
        zone.ExternalId.Should().Be("ext-123");
        zone.SiteId.Should().Be("default");
    }

    [Fact]
    public void Deserialize_ExternalZone_CorrectlyMapsSystemZone()
    {
        // Arrange - external zone has attr_no_edit=true
        var json = """
        {
            "_id": "12345external67890",
            "name": "External",
            "zone_key": "external",
            "network_ids": ["wan-network-id"],
            "default_zone": true,
            "attr_no_edit": true,
            "site_id": "default"
        }
        """;

        // Act
        var zone = JsonSerializer.Deserialize<UniFiFirewallZone>(json, JsonOptions);

        // Assert
        zone.Should().NotBeNull();
        zone!.ZoneKey.Should().Be("external");
        zone.IsDefaultZone.Should().BeTrue();
        zone.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_MinimalZone_HasDefaults()
    {
        // Arrange - minimal zone data
        var json = """
        {
            "_id": "abc123",
            "name": "Test",
            "zone_key": "internal"
        }
        """;

        // Act
        var zone = JsonSerializer.Deserialize<UniFiFirewallZone>(json, JsonOptions);

        // Assert
        zone.Should().NotBeNull();
        zone!.Id.Should().Be("abc123");
        zone.Name.Should().Be("Test");
        zone.ZoneKey.Should().Be("internal");
        zone.NetworkIds.Should().BeEmpty();
        zone.IsDefaultZone.Should().BeFalse();
        zone.IsReadOnly.Should().BeFalse();
        zone.ExternalId.Should().BeNull();
        zone.SiteId.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ZoneArray_DeserializesCorrectly()
    {
        // Arrange - array of zones as returned by API
        var json = """
        [
            {
                "_id": "zone-internal",
                "name": "Internal",
                "zone_key": "internal",
                "network_ids": ["lan-1", "lan-2"],
                "default_zone": true
            },
            {
                "_id": "zone-external",
                "name": "External",
                "zone_key": "external",
                "network_ids": ["wan"],
                "attr_no_edit": true
            },
            {
                "_id": "zone-dmz",
                "name": "DMZ",
                "zone_key": "dmz",
                "network_ids": []
            }
        ]
        """;

        // Act
        var zones = JsonSerializer.Deserialize<List<UniFiFirewallZone>>(json, JsonOptions);

        // Assert
        zones.Should().HaveCount(3);
        zones![0].ZoneKey.Should().Be("internal");
        zones[1].ZoneKey.Should().Be("external");
        zones[2].ZoneKey.Should().Be("dmz");
    }

    [Fact]
    public void Deserialize_HotspotZone_MapsCorrectly()
    {
        // Arrange - hotspot zone for guest networks
        var json = """
        {
            "_id": "zone-hotspot-123",
            "name": "Hotspot",
            "zone_key": "hotspot",
            "network_ids": ["guest-net-1"],
            "default_zone": false,
            "attr_no_edit": false
        }
        """;

        // Act
        var zone = JsonSerializer.Deserialize<UniFiFirewallZone>(json, JsonOptions);

        // Assert
        zone.Should().NotBeNull();
        zone!.ZoneKey.Should().Be("hotspot");
        zone.Name.Should().Be("Hotspot");
        zone.NetworkIds.Should().ContainSingle().Which.Should().Be("guest-net-1");
    }

    #endregion

    #region FirewallZoneKeys Constants Tests

    [Fact]
    public void FirewallZoneKeys_Internal_HasCorrectValue()
    {
        FirewallZoneKeys.Internal.Should().Be("internal");
    }

    [Fact]
    public void FirewallZoneKeys_External_HasCorrectValue()
    {
        FirewallZoneKeys.External.Should().Be("external");
    }

    [Fact]
    public void FirewallZoneKeys_Gateway_HasCorrectValue()
    {
        FirewallZoneKeys.Gateway.Should().Be("gateway");
    }

    [Fact]
    public void FirewallZoneKeys_Vpn_HasCorrectValue()
    {
        FirewallZoneKeys.Vpn.Should().Be("vpn");
    }

    [Fact]
    public void FirewallZoneKeys_Hotspot_HasCorrectValue()
    {
        FirewallZoneKeys.Hotspot.Should().Be("hotspot");
    }

    [Fact]
    public void FirewallZoneKeys_Dmz_HasCorrectValue()
    {
        FirewallZoneKeys.Dmz.Should().Be("dmz");
    }

    #endregion

    #region Model Default Value Tests

    [Fact]
    public void NewZone_HasEmptyDefaults()
    {
        // Act
        var zone = new UniFiFirewallZone();

        // Assert
        zone.Id.Should().BeEmpty();
        zone.Name.Should().BeEmpty();
        zone.ZoneKey.Should().BeEmpty();
        zone.NetworkIds.Should().BeEmpty();
        zone.IsDefaultZone.Should().BeFalse();
        zone.IsReadOnly.Should().BeFalse();
        zone.ExternalId.Should().BeNull();
        zone.SiteId.Should().BeNull();
    }

    #endregion
}
