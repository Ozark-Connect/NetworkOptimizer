using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Reading Teleport's client range out of rest/setting. It is the only place the range appears,
/// and blocking exceptions place a Teleport device in the VPN zone by it.
/// </summary>
public class UniFiTeleportSettingsTests
{
    [Fact]
    public void FromSettingsResponse_ReadsTheClientRange()
    {
        using var document = JsonDocument.Parse("""
        {
          "meta": { "rc": "ok" },
          "data": [
            { "key": "mgmt", "auto_upgrade": true },
            { "key": "teleport", "_id": "000000000000000000000001", "enabled": true, "subnet_cidr": "192.0.2.1/24" }
          ]
        }
        """);

        var teleport = UniFiTeleportSettings.FromSettingsResponse(document);

        teleport.Should().NotBeNull();
        teleport!.Enabled.Should().BeTrue();
        teleport.SubnetCidr.Should().Be("192.0.2.1/24");
    }

    [Fact]
    public void FromSettingsResponse_ReturnsNullWhenTheSectionIsAbsent()
    {
        using var document = JsonDocument.Parse("""{"meta":{"rc":"ok"},"data":[{"key":"mgmt"}]}""");

        UniFiTeleportSettings.FromSettingsResponse(document).Should().BeNull();
    }
}
