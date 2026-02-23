using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for UniFiNetworkConfig deserialization, particularly the "enabled" field behavior.
/// Some UniFi firmware versions omit the "enabled" field for WAN configs (e.g., PPPoE on UCG-Fiber).
/// </summary>
public class UniFiNetworkConfigTests
{
    [Fact]
    public void Deserialize_EnabledTrue_ReturnsTrue()
    {
        var json = """{ "name": "Internet 1", "purpose": "wan", "enabled": true }""";
        var config = JsonSerializer.Deserialize<UniFiNetworkConfig>(json)!;
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_EnabledFalse_ReturnsFalse()
    {
        var json = """{ "name": "Disabled Net", "purpose": "corporate", "enabled": false }""";
        var config = JsonSerializer.Deserialize<UniFiNetworkConfig>(json)!;
        config.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_EnabledMissing_DefaultsToTrue()
    {
        // Some firmware versions omit "enabled" for WAN configs (e.g., PPPoE on UCG-Fiber).
        // Missing should be treated as enabled, not disabled.
        var json = """{ "name": "Internet 1", "purpose": "wan", "wan_type": "pppoe", "wan_networkgroup": "WAN" }""";
        var config = JsonSerializer.Deserialize<UniFiNetworkConfig>(json)!;
        config.Enabled.Should().BeTrue("missing 'enabled' field should default to true");
    }

    [Fact]
    public void Deserialize_PppoeWanWithoutEnabled_IsIncludedInEnabledFilter()
    {
        // Real-world scenario: PPPoE WAN config from UCG-Fiber that omits the "enabled" field
        var json = """
        [
            { "name": "Internet 1", "purpose": "wan", "wan_type": "pppoe", "wan_networkgroup": "WAN", "wan_smartq_enabled": true },
            { "name": "Internet 2", "purpose": "wan", "wan_type": "dhcp", "wan_networkgroup": "WAN2" },
            { "name": "Corporate", "purpose": "corporate", "enabled": true }
        ]
        """;

        var configs = JsonSerializer.Deserialize<List<UniFiNetworkConfig>>(json)!;
        var wanConfigs = configs.Where(c => c.Purpose == "wan" && c.Enabled).ToList();

        wanConfigs.Should().HaveCount(2, "both WAN configs should be treated as enabled when field is missing");
        wanConfigs.Should().Contain(c => c.Name == "Internet 1");
        wanConfigs.Should().Contain(c => c.Name == "Internet 2");
    }

    [Fact]
    public void Deserialize_ExplicitlyDisabledWan_IsExcludedFromEnabledFilter()
    {
        var json = """
        [
            { "name": "Internet 1", "purpose": "wan", "enabled": true, "wan_networkgroup": "WAN" },
            { "name": "Internet 2", "purpose": "wan", "enabled": false, "wan_networkgroup": "WAN2" }
        ]
        """;

        var configs = JsonSerializer.Deserialize<List<UniFiNetworkConfig>>(json)!;
        var wanConfigs = configs.Where(c => c.Purpose == "wan" && c.Enabled).ToList();

        wanConfigs.Should().HaveCount(1);
        wanConfigs.First().Name.Should().Be("Internet 1");
    }
}
