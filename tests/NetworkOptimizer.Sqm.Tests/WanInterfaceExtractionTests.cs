using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// Tests for WAN interface extraction logic, particularly for PPPoE connections.
/// These tests verify the JSON parsing logic that extracts WAN interfaces from UniFi device data.
/// </summary>
public class WanInterfaceExtractionTests
{
    /// <summary>
    /// Simulates the WAN interface extraction logic from SqmService.ExtractWanInterfacesFromDeviceData
    /// to allow unit testing without requiring the full service dependencies.
    /// </summary>
    private static List<WanInterfaceTestResult> ExtractWanInterfaces(
        string deviceJson,
        Dictionary<string, bool> networkGroupToSmartq,
        Dictionary<string, string> networkGroupToWanType)
    {
        var result = new List<WanInterfaceTestResult>();

        using var doc = JsonDocument.Parse(deviceJson);
        var root = doc.RootElement;

        var devices = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) ? data : root;

        foreach (var device in devices.EnumerateArray())
        {
            var deviceType = device.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            if (deviceType != "ugw" && deviceType != "udm" && deviceType != "uxg")
                continue;

            // Build ifname -> networkgroup lookup from ethernet_overrides
            var ifnameToNetworkGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (device.TryGetProperty("ethernet_overrides", out var ethOverrides) &&
                ethOverrides.ValueKind == JsonValueKind.Array)
            {
                foreach (var ov in ethOverrides.EnumerateArray())
                {
                    var ifn = ov.TryGetProperty("ifname", out var ifnProp) ? ifnProp.GetString() : null;
                    var ng = ov.TryGetProperty("networkgroup", out var ngProp) ? ngProp.GetString() : null;
                    if (!string.IsNullOrEmpty(ifn) && !string.IsNullOrEmpty(ng))
                    {
                        ifnameToNetworkGroup[ifn] = ng;
                    }
                }
            }

            // Check for wan1, wan2, wan3, wan4
            for (int i = 1; i <= 4; i++)
            {
                var wanKey = $"wan{i}";
                if (device.TryGetProperty(wanKey, out var wanObj))
                {
                    // Get the uplink interface name (actual interface for SQM)
                    string? uplinkIfname = null;
                    if (wanObj.TryGetProperty("uplink_ifname", out var uplinkProp))
                        uplinkIfname = uplinkProp.GetString();

                    if (string.IsNullOrEmpty(uplinkIfname))
                        continue;

                    // Get the physical interface name (for networkgroup lookup)
                    string? physicalIfname = null;
                    if (wanObj.TryGetProperty("ifname", out var ifnameProp))
                        physicalIfname = ifnameProp.GetString();
                    if (string.IsNullOrEmpty(physicalIfname) && wanObj.TryGetProperty("name", out var nameProp))
                        physicalIfname = nameProp.GetString();

                    // Get networkgroup using physical interface
                    string? networkGroup = null;
                    var lookupIfname = physicalIfname ?? uplinkIfname;
                    if (ifnameToNetworkGroup.TryGetValue(lookupIfname, out var ng))
                        networkGroup = ng;

                    // Check SmartQ status
                    var smartqEnabled = !string.IsNullOrEmpty(networkGroup) &&
                        networkGroupToSmartq.TryGetValue(networkGroup, out var sqEnabled) && sqEnabled;

                    // Get WAN type
                    var wanType = "dhcp";
                    if (!string.IsNullOrEmpty(networkGroup) &&
                        networkGroupToWanType.TryGetValue(networkGroup, out var wt))
                    {
                        wanType = wt;
                    }

                    var tcInterface = $"ifb{uplinkIfname}";

                    result.Add(new WanInterfaceTestResult
                    {
                        WanKey = wanKey,
                        Interface = uplinkIfname,
                        PhysicalInterface = physicalIfname,
                        TcInterface = tcInterface,
                        NetworkGroup = networkGroup,
                        SmartqEnabled = smartqEnabled,
                        WanType = wanType
                    });
                }
            }

            if (result.Count > 0)
                break;
        }

        return result;
    }

    private class WanInterfaceTestResult
    {
        public string WanKey { get; set; } = "";
        public string Interface { get; set; } = "";
        public string? PhysicalInterface { get; set; }
        public string TcInterface { get; set; } = "";
        public string? NetworkGroup { get; set; }
        public bool SmartqEnabled { get; set; }
        public string WanType { get; set; } = "";
    }

    [Fact]
    public void ExtractWanInterfaces_PppoeConnection_UsesPhysicalInterfaceForNetworkGroupLookup()
    {
        // Arrange - PPPoE WAN where uplink_ifname is the tunnel (ppp3) but physical is eth6
        var deviceJson = """
        [{
            "type": "udm",
            "ethernet_overrides": [
                { "ifname": "eth4", "networkgroup": "WAN" },
                { "ifname": "eth6", "networkgroup": "WAN2" }
            ],
            "wan1": {
                "uplink_ifname": "eth4",
                "ifname": "eth4",
                "ip": "192.0.2.100"
            },
            "wan2": {
                "uplink_ifname": "ppp3",
                "ifname": "eth6",
                "name": "eth6",
                "ip": "198.51.100.50"
            }
        }]
        """;

        var networkGroupToSmartq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = true,
            ["WAN2"] = true
        };

        var networkGroupToWanType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = "dhcp",
            ["WAN2"] = "pppoe"
        };

        // Act
        var result = ExtractWanInterfaces(deviceJson, networkGroupToSmartq, networkGroupToWanType);

        // Assert
        result.Should().HaveCount(2);

        // WAN1 - standard DHCP connection
        var wan1 = result.First(r => r.WanKey == "wan1");
        wan1.Interface.Should().Be("eth4");
        wan1.PhysicalInterface.Should().Be("eth4");
        wan1.TcInterface.Should().Be("ifbeth4");
        wan1.NetworkGroup.Should().Be("WAN");
        wan1.SmartqEnabled.Should().BeTrue();
        wan1.WanType.Should().Be("dhcp");

        // WAN2 - PPPoE connection (tunnel interface differs from physical)
        var wan2 = result.First(r => r.WanKey == "wan2");
        wan2.Interface.Should().Be("ppp3", "PPPoE should use the tunnel interface for SQM");
        wan2.PhysicalInterface.Should().Be("eth6", "Physical interface should be extracted for networkgroup lookup");
        wan2.TcInterface.Should().Be("ifbppp3", "IFB should be based on the tunnel interface");
        wan2.NetworkGroup.Should().Be("WAN2", "NetworkGroup should be found via physical interface eth6");
        wan2.SmartqEnabled.Should().BeTrue("SmartQ should be enabled via networkgroup lookup");
        wan2.WanType.Should().Be("pppoe", "WAN type should be pppoe from network config");
    }

    [Fact]
    public void ExtractWanInterfaces_PppoeWithoutPhysicalIfname_FallsBackToName()
    {
        // Arrange - PPPoE WAN where ifname is missing but name is present
        var deviceJson = """
        [{
            "type": "udm",
            "ethernet_overrides": [
                { "ifname": "eth6", "networkgroup": "WAN" }
            ],
            "wan1": {
                "uplink_ifname": "ppp0",
                "name": "eth6"
            }
        }]
        """;

        var networkGroupToSmartq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = true
        };

        var networkGroupToWanType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = "pppoe"
        };

        // Act
        var result = ExtractWanInterfaces(deviceJson, networkGroupToSmartq, networkGroupToWanType);

        // Assert
        result.Should().HaveCount(1);
        var wan = result.First();
        wan.Interface.Should().Be("ppp0");
        wan.PhysicalInterface.Should().Be("eth6", "Should fall back to 'name' field when 'ifname' is missing");
        wan.TcInterface.Should().Be("ifbppp0");
        wan.NetworkGroup.Should().Be("WAN");
        wan.SmartqEnabled.Should().BeTrue();
        wan.WanType.Should().Be("pppoe");
    }

    [Fact]
    public void ExtractWanInterfaces_PppoeWithSmartqDisabled_ShowsAsNotEligible()
    {
        // Arrange - PPPoE WAN where SmartQ is not enabled
        var deviceJson = """
        [{
            "type": "udm",
            "ethernet_overrides": [
                { "ifname": "eth6", "networkgroup": "WAN2" }
            ],
            "wan1": {
                "uplink_ifname": "ppp3",
                "ifname": "eth6"
            }
        }]
        """;

        var networkGroupToSmartq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN2"] = false  // SmartQ disabled
        };

        var networkGroupToWanType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN2"] = "pppoe"
        };

        // Act
        var result = ExtractWanInterfaces(deviceJson, networkGroupToSmartq, networkGroupToWanType);

        // Assert
        result.Should().HaveCount(1);
        var wan = result.First();
        wan.Interface.Should().Be("ppp3");
        wan.TcInterface.Should().Be("ifbppp3");
        wan.SmartqEnabled.Should().BeFalse("SmartQ is disabled for this WAN");
        wan.WanType.Should().Be("pppoe");
    }

    [Fact]
    public void ExtractWanInterfaces_StandardDhcp_WorksAsExpected()
    {
        // Arrange - Standard DHCP WAN (no PPPoE)
        var deviceJson = """
        [{
            "type": "ugw",
            "ethernet_overrides": [
                { "ifname": "eth0", "networkgroup": "WAN" }
            ],
            "wan1": {
                "uplink_ifname": "eth0",
                "ifname": "eth0",
                "ip": "203.0.113.50"
            }
        }]
        """;

        var networkGroupToSmartq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = true
        };

        var networkGroupToWanType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = "dhcp"
        };

        // Act
        var result = ExtractWanInterfaces(deviceJson, networkGroupToSmartq, networkGroupToWanType);

        // Assert
        result.Should().HaveCount(1);
        var wan = result.First();
        wan.Interface.Should().Be("eth0");
        wan.PhysicalInterface.Should().Be("eth0");
        wan.TcInterface.Should().Be("ifbeth0");
        wan.SmartqEnabled.Should().BeTrue();
        wan.WanType.Should().Be("dhcp");
    }

    [Fact]
    public void ExtractWanInterfaces_MultipleWansWithMixedTypes_ExtractsAllCorrectly()
    {
        // Arrange - Multiple WANs: DHCP, PPPoE, and Static
        var deviceJson = """
        [{
            "type": "uxg",
            "ethernet_overrides": [
                { "ifname": "eth4", "networkgroup": "WAN" },
                { "ifname": "eth5", "networkgroup": "WAN3" },
                { "ifname": "eth6", "networkgroup": "WAN2" }
            ],
            "wan1": {
                "uplink_ifname": "eth4",
                "ifname": "eth4",
                "ip": "192.0.2.10"
            },
            "wan2": {
                "uplink_ifname": "ppp3",
                "ifname": "eth6",
                "ip": "198.51.100.20"
            },
            "wan3": {
                "uplink_ifname": "eth5",
                "ifname": "eth5",
                "ip": "203.0.113.30"
            }
        }]
        """;

        var networkGroupToSmartq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = true,
            ["WAN2"] = true,
            ["WAN3"] = true
        };

        var networkGroupToWanType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = "dhcp",
            ["WAN2"] = "pppoe",
            ["WAN3"] = "static"
        };

        // Act
        var result = ExtractWanInterfaces(deviceJson, networkGroupToSmartq, networkGroupToWanType);

        // Assert
        result.Should().HaveCount(3);

        var wan1 = result.First(r => r.WanKey == "wan1");
        wan1.Interface.Should().Be("eth4");
        wan1.TcInterface.Should().Be("ifbeth4");
        wan1.WanType.Should().Be("dhcp");

        var wan2 = result.First(r => r.WanKey == "wan2");
        wan2.Interface.Should().Be("ppp3");
        wan2.TcInterface.Should().Be("ifbppp3");
        wan2.WanType.Should().Be("pppoe");

        var wan3 = result.First(r => r.WanKey == "wan3");
        wan3.Interface.Should().Be("eth5");
        wan3.TcInterface.Should().Be("ifbeth5");
        wan3.WanType.Should().Be("static");
    }

    [Fact]
    public void ExtractWanInterfaces_PppoeNetworkGroupNotInEthernetOverrides_FallsBackToUplinkIfname()
    {
        // Arrange - Edge case: ethernet_overrides doesn't have the physical interface
        var deviceJson = """
        [{
            "type": "udm",
            "ethernet_overrides": [
                { "ifname": "eth4", "networkgroup": "WAN" }
            ],
            "wan1": {
                "uplink_ifname": "ppp3",
                "ifname": "eth6"
            }
        }]
        """;

        var networkGroupToSmartq = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = true
        };

        var networkGroupToWanType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["WAN"] = "dhcp"
        };

        // Act
        var result = ExtractWanInterfaces(deviceJson, networkGroupToSmartq, networkGroupToWanType);

        // Assert
        result.Should().HaveCount(1);
        var wan = result.First();
        wan.Interface.Should().Be("ppp3");
        wan.TcInterface.Should().Be("ifbppp3");
        wan.NetworkGroup.Should().BeNull("eth6 is not in ethernet_overrides");
        wan.SmartqEnabled.Should().BeFalse("No networkgroup means SmartQ status can't be determined");
        wan.WanType.Should().Be("dhcp", "Falls back to default when networkgroup not found");
    }
}
