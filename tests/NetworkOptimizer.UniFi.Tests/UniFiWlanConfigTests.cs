using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for UniFiWlanConfig model JSON deserialization.
/// </summary>
public class UniFiWlanConfigTests
{
    [Fact]
    public void Deserialize_MloEnabled_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "enabled": true,
            "mlo_enabled": true
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.MloEnabled.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_MloDisabled_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "enabled": true,
            "mlo_enabled": false
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.MloEnabled.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_MloMissing_DefaultsToFalse()
    {
        // Arrange - mlo_enabled not present in JSON
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "enabled": true
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.MloEnabled.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_ApGroupModeAll_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "enabled": true,
            "ap_group_mode": "all"
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.ApGroupMode.Should().Be("all");
    }

    [Fact]
    public void Deserialize_ApGroupIds_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "enabled": true,
            "ap_group_ids": ["group1", "group2"]
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.ApGroupIds.Should().NotBeNull();
        config!.ApGroupIds.Should().HaveCount(2);
        config!.ApGroupIds.Should().Contain("group1");
        config!.ApGroupIds.Should().Contain("group2");
    }

    [Fact]
    public void Deserialize_WlanBands_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "enabled": true,
            "wlan_bands": ["2g", "5g", "6g"]
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.WlanBands.Should().NotBeNull();
        config!.WlanBands.Should().HaveCount(3);
        config!.WlanBands.Should().Contain("6g");
    }

    [Fact]
    public void Deserialize_FullSample_IgnoresUnknownFields()
    {
        // Arrange - Sample with many fields including sensitive ones that should be ignored
        var json = """
        {
            "_id": "68c9b96e53d37844b1051ff9",
            "name": "HomeNetwork",
            "enabled": true,
            "is_guest": false,
            "hide_ssid": false,
            "security": "wpapsk",
            "mlo_enabled": true,
            "fast_roaming_enabled": true,
            "bss_transition": true,
            "l2_isolation": false,
            "no2ghz_oui": true,
            "wlan_bands": ["2g", "5g", "6g"],
            "ap_group_mode": "all",
            "ap_group_ids": ["68118f01d70eea4ec69a1924"],
            "minrate_ng_enabled": true,
            "minrate_ng_data_rate_kbps": 1000,
            "x_passphrase": "should-be-ignored",
            "x_iapp_key": "should-be-ignored",
            "private_preshared_keys": [],
            "sae_psk": [],
            "unknown_future_field": "should-be-ignored"
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert
        config.Should().NotBeNull();
        config!.Id.Should().Be("68c9b96e53d37844b1051ff9");
        config!.Name.Should().Be("HomeNetwork");
        config!.Enabled.Should().BeTrue();
        config!.MloEnabled.Should().BeTrue();
        config!.FastRoamingEnabled.Should().BeTrue();
        config!.BssTransition.Should().BeTrue();
        config!.No2ghzOui.Should().BeTrue();
        config!.ApGroupMode.Should().Be("all");
    }

    [Fact]
    public void Deserialize_DoesNotExposeSensitiveFields()
    {
        // Arrange
        var json = """
        {
            "_id": "test123",
            "name": "TestNetwork",
            "x_passphrase": "secret-password",
            "x_iapp_key": "secret-key"
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<UniFiWlanConfig>(json);

        // Assert - Verify sensitive fields are not accessible
        config.Should().NotBeNull();
        var type = typeof(UniFiWlanConfig);
        type.GetProperty("XPassphrase").Should().BeNull("x_passphrase should not be mapped");
        type.GetProperty("Passphrase").Should().BeNull("passphrase should not be mapped");
        type.GetProperty("XIappKey").Should().BeNull("x_iapp_key should not be mapped");
    }
}
