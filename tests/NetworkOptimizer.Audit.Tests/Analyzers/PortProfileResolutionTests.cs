using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

/// <summary>
/// Tests for port profile resolution in PortSecurityAnalyzer.
/// When a port has a portconf_id, the forward mode should be resolved from the profile.
/// </summary>
public class PortProfileResolutionTests
{
    private readonly PortSecurityAnalyzer _engine;

    public PortProfileResolutionTests()
    {
        _engine = new PortSecurityAnalyzer(NullLogger<PortSecurityAnalyzer>.Instance);
    }

    [Fact]
    public void ExtractSwitches_PortWithProfile_ResolvesForwardModeFromProfile()
    {
        // Port has forward="all" but profile has forward="disabled"
        // The profile setting should take precedence
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 4,
                        ""name"": ""Port 4"",
                        ""portconf_id"": ""profile-disabled-123"",
                        ""forward"": ""all"",
                        ""up"": false
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-disabled-123",
                Name = "Disable Unused Ports",
                Forward = "disabled"
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("disabled", "profile forward mode should override port's forward mode");
    }

    [Fact]
    public void ExtractSwitches_PortWithProfileButNoForward_UsesPortForwardMode()
    {
        // Profile exists but has null Forward - use port's forward mode
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""portconf_id"": ""profile-no-forward"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-no-forward",
                Name = "Some Profile",
                Forward = null
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("native", "when profile has no forward setting, port's value should be used");
    }

    [Fact]
    public void ExtractSwitches_PortWithMissingProfile_UsesPortForwardMode()
    {
        // Port references a profile ID that doesn't exist in the profiles list
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""portconf_id"": ""nonexistent-profile"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>();

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("native", "when profile not found, port's value should be used");
    }

    [Fact]
    public void ExtractSwitches_PortWithoutProfile_UsesPortForwardMode()
    {
        // Port has no portconf_id - standard case
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""forward"": ""all"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "some-profile",
                Name = "Some Profile",
                Forward = "disabled"
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("all", "port without profile reference should use its own forward mode");
    }

    [Fact]
    public void ExtractSwitches_NoProfilesProvided_UsesPortForwardMode()
    {
        // Port has portconf_id but no profiles were provided (null)
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 4,
                        ""portconf_id"": ""profile-123"",
                        ""forward"": ""all"",
                        ""up"": false
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        // Call without port profiles (uses overload that doesn't accept profiles)
        var result = _engine.ExtractSwitches(deviceData, networks);

        result[0].Ports[0].ForwardMode.Should().Be("all", "without profiles provided, port's value should be used");
    }

    [Fact]
    public void ExtractSwitches_CaseInsensitiveProfileLookup()
    {
        // Profile ID lookup should be case-insensitive
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""portconf_id"": ""PROFILE-UPPER"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-upper",  // lowercase
                Name = "Test Profile",
                Forward = "disabled"
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("disabled", "profile lookup should be case-insensitive");
    }

    [Fact]
    public void ExtractSwitches_MultiplePortsWithDifferentProfiles()
    {
        // Multiple ports referencing different profiles
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""portconf_id"": ""profile-disabled"",
                        ""forward"": ""all"",
                        ""up"": false
                    },
                    {
                        ""port_idx"": 2,
                        ""portconf_id"": ""profile-trunk"",
                        ""forward"": ""native"",
                        ""up"": true
                    },
                    {
                        ""port_idx"": 3,
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new() { Id = "profile-disabled", Name = "Disabled", Forward = "disabled" },
            new() { Id = "profile-trunk", Name = "Trunk", Forward = "all" }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].ForwardMode.Should().Be("disabled", "port 1 should use disabled profile");
        result[0].Ports[1].ForwardMode.Should().Be("all", "port 2 should use trunk profile");
        result[0].Ports[2].ForwardMode.Should().Be("native", "port 3 should use its own forward mode");
    }

    #region End-to-End Integration Tests

    /// <summary>
    /// Integration test: Verifies that a port with a "Disable Unused Ports" profile
    /// is NOT flagged by UnusedPortRule after profile resolution.
    /// This is the exact bug scenario from issue #63.
    /// </summary>
    [Fact]
    public void Integration_PortWithDisabledProfile_NotFlaggedByUnusedPortRule()
    {
        // Arrange: Port 4 has forward="all" in port_table but profile sets forward="disabled"
        // This matches the real-world scenario where UniFi returns forward="all" but the
        // profile should override it to "disabled"
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Tiny Home - Main"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 4,
                        ""name"": ""Port 4"",
                        ""portconf_id"": ""6962eb8fbdb4d8de9a30f5c1"",
                        ""forward"": ""all"",
                        ""up"": false
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "6962eb8fbdb4d8de9a30f5c1",
                Name = "Disable Unused Ports",
                Forward = "disabled"
            }
        };

        // Act: Extract switches with profile resolution, then analyze ports
        var switches = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);
        var issues = _engine.AnalyzePorts(switches, networks);

        // Assert: Port should have resolved forward mode and NOT be flagged
        switches[0].Ports[0].ForwardMode.Should().Be("disabled",
            "forward mode should be resolved from profile");

        issues.Should().NotContain(i => i.Type == "UNUSED-PORT-001" && i.Port == "4",
            "port with disabled profile should NOT be flagged as unused");
    }

    /// <summary>
    /// Integration test: Verifies that without profile resolution, the same port
    /// WOULD be flagged (proving the fix is necessary).
    /// </summary>
    [Fact]
    public void Integration_PortWithoutProfileResolution_WouldBeFlagged()
    {
        // Arrange: Same port data but NO profiles provided
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Tiny Home - Main"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 4,
                        ""name"": ""Port 4"",
                        ""portconf_id"": ""6962eb8fbdb4d8de9a30f5c1"",
                        ""forward"": ""all"",
                        ""up"": false
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        // Act: Extract switches WITHOUT profiles, then analyze
        var switches = _engine.ExtractSwitches(deviceData, networks);
        var issues = _engine.AnalyzePorts(switches, networks);

        // Assert: Port should retain forward="all" and BE flagged
        switches[0].Ports[0].ForwardMode.Should().Be("all",
            "without profile resolution, port keeps its base forward mode");

        issues.Should().Contain(i => i.Type == "UNUSED-PORT-001" && i.Port == "4",
            "port without profile resolution SHOULD be flagged as unused");
    }

    /// <summary>
    /// Integration test: Multiple ports - some with profiles, some without.
    /// Verifies selective profile resolution works correctly.
    /// </summary>
    [Fact]
    public void Integration_MixedPorts_OnlyProfiledPortsResolved()
    {
        // Recent timestamp for custom-named port (within 45-day grace period)
        var recentTimestamp = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();

        var deviceData = JsonDocument.Parse($@"[
            {{
                ""type"": ""usw"",
                ""name"": ""Office Switch"",
                ""mac"": ""11:22:33:44:55:66"",
                ""port_table"": [
                    {{
                        ""port_idx"": 1,
                        ""name"": ""Port 1"",
                        ""portconf_id"": ""profile-disabled"",
                        ""forward"": ""all"",
                        ""up"": false
                    }},
                    {{
                        ""port_idx"": 2,
                        ""name"": ""Port 2"",
                        ""forward"": ""all"",
                        ""up"": false
                    }},
                    {{
                        ""port_idx"": 3,
                        ""name"": ""Printer"",
                        ""forward"": ""native"",
                        ""up"": false,
                        ""last_connection"": {{
                            ""last_seen"": {recentTimestamp}
                        }}
                    }}
                ]
            }}
        ]").RootElement;
        var networks = new List<NetworkInfo>();
        var portProfiles = new List<UniFiPortProfile>
        {
            new() { Id = "profile-disabled", Name = "Disabled", Forward = "disabled" }
        };

        var switches = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);
        var issues = _engine.AnalyzePorts(switches, networks);

        // Port 1: Has profile -> disabled -> NOT flagged
        switches[0].Ports[0].ForwardMode.Should().Be("disabled");
        issues.Should().NotContain(i => i.Port == "1", "port 1 has disabled profile");

        // Port 2: No profile, default name, down, forward=all -> FLAGGED
        switches[0].Ports[1].ForwardMode.Should().Be("all");
        issues.Should().Contain(i => i.Type == "UNUSED-PORT-001" && i.Port == "2",
            "port 2 has no profile and default name");

        // Port 3: No profile but custom name -> NOT flagged (different rule)
        issues.Should().NotContain(i => i.Port == "3", "port 3 has custom name 'Printer'");
    }

    /// <summary>
    /// Integration test: Custom-named port with OLD timestamp (beyond 45-day grace period)
    /// SHOULD be flagged as unused. This is the opposite of the test above.
    /// </summary>
    [Fact]
    public void Integration_CustomNamedPort_WithOldTimestamp_ShouldBeFlagged()
    {
        // Old timestamp - beyond 45-day grace period for named ports
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeSeconds();

        var deviceData = JsonDocument.Parse($@"[
            {{
                ""type"": ""usw"",
                ""name"": ""Office Switch"",
                ""mac"": ""11:22:33:44:55:66"",
                ""port_table"": [
                    {{
                        ""port_idx"": 1,
                        ""name"": ""Old Printer"",
                        ""forward"": ""native"",
                        ""up"": false,
                        ""last_connection"": {{
                            ""last_seen"": {oldTimestamp}
                        }}
                    }}
                ]
            }}
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var switches = _engine.ExtractSwitches(deviceData, networks);
        var issues = _engine.AnalyzePorts(switches, networks);

        // Port has custom name but OLD timestamp -> SHOULD be flagged
        issues.Should().Contain(i => i.Type == "UNUSED-PORT-001" && i.Port == "1",
            "custom-named port with timestamp older than 45 days should be flagged");
    }

    /// <summary>
    /// Integration test: Custom-named port with NO timestamp SHOULD be flagged
    /// (no timestamp means no recent activity evidence, so flag for review).
    /// </summary>
    [Fact]
    public void Integration_CustomNamedPort_WithNoTimestamp_ShouldBeFlagged()
    {
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Office Switch"",
                ""mac"": ""11:22:33:44:55:66"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""name"": ""Conference Room TV"",
                        ""forward"": ""native"",
                        ""up"": false
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>();

        var switches = _engine.ExtractSwitches(deviceData, networks);
        var issues = _engine.AnalyzePorts(switches, networks);

        // Port has custom name but no timestamp -> flagged (no activity evidence)
        issues.Should().Contain(i => i.Type == "UNUSED-PORT-001" && i.Port == "1",
            "custom-named port without timestamp should be flagged since there's no activity evidence");
    }

    #endregion

    #region VLAN Resolution Tests

    /// <summary>
    /// Tests that a port profile's VLAN takes precedence over the port's base native_networkconf_id.
    /// This is the bug fix for AP ethernet ports where UniFi returns Default LAN as the port's
    /// native_networkconf_id even when a port profile with a different VLAN is applied.
    /// </summary>
    [Fact]
    public void ExtractSwitches_PortWithProfileVlan_ProfileVlanTakesPrecedence()
    {
        // Port has native_networkconf_id="default-lan-id" but profile has NativeNetworkId="iot-vlan-id"
        // The profile setting should take precedence
        // Note: Using type="usw" (switch) since UAPs with <=2 ports are skipped
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""name"": ""Port 1"",
                        ""portconf_id"": ""profile-iot-vlan"",
                        ""native_networkconf_id"": ""default-lan-id"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new() { Id = "default-lan-id", Name = "Default", VlanId = 1, Subnet = "10.10.10.0/24" },
            new() { Id = "iot-vlan-id", Name = "IoT", VlanId = 30, Subnet = "10.10.30.0/24" }
        };
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-iot-vlan",
                Name = "IoT Devices",
                NativeNetworkId = "iot-vlan-id"
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].NativeNetworkId.Should().Be("iot-vlan-id",
            "profile VLAN should override port's native_networkconf_id");
    }

    /// <summary>
    /// Tests that when a profile has no VLAN set, the port's native_networkconf_id is used.
    /// </summary>
    [Fact]
    public void ExtractSwitches_PortWithProfileButNoVlan_UsesPortNativeNetworkId()
    {
        // Profile exists but has null NativeNetworkId - use port's native_networkconf_id
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""portconf_id"": ""profile-no-vlan"",
                        ""native_networkconf_id"": ""management-vlan-id"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new() { Id = "management-vlan-id", Name = "Management", VlanId = 10, Subnet = "10.10.10.0/24" }
        };
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-no-vlan",
                Name = "Security Profile",
                NativeNetworkId = null,
                PortSecurityEnabled = true
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].NativeNetworkId.Should().Be("management-vlan-id",
            "when profile has no VLAN setting, port's native_networkconf_id should be used");
    }

    /// <summary>
    /// Tests that when a port has no native_networkconf_id and profile has one, profile is used.
    /// </summary>
    [Fact]
    public void ExtractSwitches_PortWithoutVlanAndProfileHasVlan_UsesProfileVlan()
    {
        // Port has no native_networkconf_id, profile provides one
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""portconf_id"": ""profile-with-vlan"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new() { Id = "guest-vlan-id", Name = "Guest", VlanId = 50, Subnet = "10.10.50.0/24" }
        };
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-with-vlan",
                Name = "Guest Port",
                NativeNetworkId = "guest-vlan-id"
            }
        };

        var result = _engine.ExtractSwitches(deviceData, networks, null, null, portProfiles);

        result[0].Ports[0].NativeNetworkId.Should().Be("guest-vlan-id",
            "profile VLAN should be used when port has no native_networkconf_id");
    }

    /// <summary>
    /// Integration test: Verifies that WiredSubnetMismatchRule uses the profile's VLAN
    /// when validating client IP addresses, not the port's base VLAN.
    /// This is the exact bug scenario where a U6 Enterprise IW port profile assigns
    /// a VLAN but the audit incorrectly compares against the Default LAN.
    /// </summary>
    [Fact]
    public void Integration_PortWithProfileVlan_WiredSubnetMismatchUsesProfileVlan()
    {
        // Arrange: Port has Default LAN (10.10.10.0/24) as base, but profile assigns IoT VLAN (10.10.30.0/24)
        // Client has IP 10.10.30.50 which is correct for IoT VLAN
        // Without fix: Would flag as mismatch (10.10.30.50 not in Default's 10.10.10.0/24)
        // With fix: Should NOT flag (10.10.30.50 IS in IoT's 10.10.30.0/24)
        // Note: U6 Enterprise IW has 4 ports, need 3+ ports for UAP to not be skipped
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""uap"",
                ""name"": ""U6 Enterprise IW"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""name"": ""LAN 1"",
                        ""portconf_id"": ""profile-iot"",
                        ""native_networkconf_id"": ""default-lan-id"",
                        ""forward"": ""native"",
                        ""up"": true
                    },
                    {
                        ""port_idx"": 2,
                        ""name"": ""LAN 2"",
                        ""forward"": ""native"",
                        ""up"": false
                    },
                    {
                        ""port_idx"": 3,
                        ""name"": ""LAN 3"",
                        ""forward"": ""native"",
                        ""up"": false
                    },
                    {
                        ""port_idx"": 4,
                        ""name"": ""Uplink"",
                        ""forward"": ""native"",
                        ""up"": true,
                        ""is_uplink"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new() { Id = "default-lan-id", Name = "Default", VlanId = 1, Subnet = "10.10.10.0/24" },
            new() { Id = "iot-vlan-id", Name = "IoT", VlanId = 30, Subnet = "10.10.30.0/24" }
        };
        var portProfiles = new List<UniFiPortProfile>
        {
            new()
            {
                Id = "profile-iot",
                Name = "IoT Devices",
                NativeNetworkId = "iot-vlan-id"
            }
        };
        var clients = new List<UniFiClientResponse>
        {
            new()
            {
                Mac = "11:22:33:44:55:66",
                Ip = "10.10.30.50",  // Correct IP for IoT VLAN
                IsWired = true,
                SwMac = "aa:bb:cc:dd:ee:ff",
                SwPort = 1
            }
        };

        // Act
        var switches = _engine.ExtractSwitches(deviceData, networks, clients, null, portProfiles);
        var issues = _engine.AnalyzePorts(switches, networks);

        // Assert: Port should have IoT VLAN and client should NOT be flagged
        switches[0].Ports[0].NativeNetworkId.Should().Be("iot-vlan-id",
            "port should have profile's VLAN");
        issues.Should().NotContain(i => i.Type == "WIRED-SUBNET-001",
            "client IP matches profile's VLAN subnet, should not be flagged");
    }

    /// <summary>
    /// Integration test: Without profiles passed, the port keeps its base VLAN.
    /// This verifies that profile resolution only happens when profiles are provided.
    /// </summary>
    [Fact]
    public void Integration_PortWithoutProfilesProvided_KeepsBaseVlan()
    {
        // Port has native_networkconf_id set but no profiles provided for resolution
        var deviceData = JsonDocument.Parse(@"[
            {
                ""type"": ""usw"",
                ""name"": ""Switch"",
                ""mac"": ""aa:bb:cc:dd:ee:ff"",
                ""port_table"": [
                    {
                        ""port_idx"": 1,
                        ""name"": ""Port 1"",
                        ""portconf_id"": ""profile-iot"",
                        ""native_networkconf_id"": ""default-lan-id"",
                        ""forward"": ""native"",
                        ""up"": true
                    }
                ]
            }
        ]").RootElement;
        var networks = new List<NetworkInfo>
        {
            new() { Id = "default-lan-id", Name = "Default", VlanId = 1, Subnet = "10.10.10.0/24" },
            new() { Id = "iot-vlan-id", Name = "IoT", VlanId = 30, Subnet = "10.10.30.0/24" }
        };

        // Act: Extract WITHOUT profiles - port should keep base VLAN
        var switches = _engine.ExtractSwitches(deviceData, networks, null, null, null);

        // Assert: Without profile resolution, port keeps its base native_networkconf_id
        switches[0].Ports[0].NativeNetworkId.Should().Be("default-lan-id",
            "without profiles provided, port keeps its base native_networkconf_id");
    }

    #endregion
}
