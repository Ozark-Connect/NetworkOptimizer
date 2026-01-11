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
}
