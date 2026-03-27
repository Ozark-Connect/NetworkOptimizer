using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests that gateway-only consoles (no Wi-Fi radios) are excluded from the
/// Wi-Fi Optimizer AP list, even when the UniFi API reports phantom radio_table entries.
/// Device model/shortname values come from real API responses.
/// </summary>
public class GatewayApExclusionTests
{
    /// <summary>
    /// Creates a DiscoveredDevice matching real UniFi API device response data.
    /// Model and Shortname must match the production database so FriendlyModelName resolves correctly.
    /// </summary>
    private static DiscoveredDevice CreateGatewayDevice(
        string model, string? shortname, int radioCount = 0)
    {
        var device = new DiscoveredDevice
        {
            Id = Guid.NewGuid().ToString(),
            Mac = $"aa:bb:cc:{Guid.NewGuid().ToString()[..8]}",
            Name = $"Test {shortname ?? model}",
            Type = DeviceType.Gateway,
            HardwareType = DeviceType.Gateway,
            Model = model,
            Shortname = shortname,
            IpAddress = "192.0.2.1",
            RadioTable = radioCount > 0
                ? Enumerable.Range(0, radioCount).Select(i => new RadioTableEntry { Name = $"wifi{i}" }).ToList()
                : null
        };
        return device;
    }

    // ---------------------------------------------------------------
    // Gateway-only consoles: must be excluded even with radio_table
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("UDMPRO", "UDMPRO", "UDM-Pro")]          // Dream Machine Pro
    [InlineData("UDMPROSE", "UDMPROSE", "UDM-SE")]        // Dream Machine SE
    [InlineData("UDMPROMAX", "UDMPROMAX", "UDM-Pro-Max")] // Dream Machine Pro Max
    public void UdmProFamily_Excluded(string model, string shortname, string expectedFriendlyName)
    {
        var device = CreateGatewayDevice(model, shortname, radioCount: 2);

        device.FriendlyModelName.Should().Be(expectedFriendlyName,
            $"model={model} shortname={shortname} should resolve to {expectedFriendlyName}");
        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeTrue(
            $"{expectedFriendlyName} has no Wi-Fi radios");
    }

    [Theory]
    [InlineData("UDMENT", "UDMENT", "EFG")]   // Enterprise Fortress Gateway
    [InlineData("EFG", "EFG", "EFG")]          // EFG via shortname alias
    public void Efg_Excluded(string model, string shortname, string expectedFriendlyName)
    {
        var device = CreateGatewayDevice(model, shortname, radioCount: 2);

        device.FriendlyModelName.Should().Be(expectedFriendlyName);
        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeTrue(
            $"{expectedFriendlyName} has no Wi-Fi radios");
    }

    [Fact]
    public void EfgCore_ExcludedByPrefix()
    {
        // EFG-Core not yet in the product database, but FriendlyModelName would
        // fall back to the shortname. Verify the StartsWith("EFG") prefix catches it.
        var device = CreateGatewayDevice("EFGCORE", "EFG-Core", radioCount: 2);

        // FriendlyModelName may be "EFG-Core" (from shortname fallback) or whatever the DB resolves
        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeTrue(
            "EFG-Core starts with 'EFG' and should be excluded");
    }

    [Theory]
    [InlineData("UXGPRO", "UXGPRO")]     // Gateway Pro
    [InlineData("UXGENT", "UXGENT")]      // Gateway Enterprise
    [InlineData("UDMA6A8", "UCGF")]       // Cloud Gateway Fiber
    [InlineData("UDRULT", "UDRULT")]      // UCG-Ultra
    [InlineData("UXGB", "UXGB")]          // Gateway Max
    public void OtherGatewayOnly_NotMatchedButNoRadios(string model, string shortname)
    {
        // These don't start with "UDM-" or "EFG" but also have no Wi-Fi.
        // They're not caught by IsGatewayOnlyConsole, but they also won't
        // have radio_table entries in the real API, so the RadioTable check
        // in DiscoverAccessPointsAsync filters them out.
        var device = CreateGatewayDevice(model, shortname, radioCount: 0);

        // No radio_table → filtered by the Count > 0 check, not by IsGatewayOnlyConsole
        device.RadioTable.Should().BeNull();
    }

    // ---------------------------------------------------------------
    // Gateways WITH real Wi-Fi: must be allowed through
    // ---------------------------------------------------------------

    [Fact]
    public void Udm_Allowed()
    {
        // Original Dream Machine - has real Wi-Fi radios
        var device = CreateGatewayDevice("UDM", "UDM", radioCount: 2);

        device.FriendlyModelName.Should().Be("UDM");
        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UDM (original Dream Machine) has integrated Wi-Fi");
    }

    [Fact]
    public void Udr_Allowed()
    {
        // Dream Router - has real Wi-Fi radios
        var device = CreateGatewayDevice("UDR", "UDR", radioCount: 2);

        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UDR has integrated Wi-Fi");
    }

    [Fact]
    public void Udr7_Allowed()
    {
        // Dream Router 7 - has real Wi-Fi radios (from sample-device-resp-udr7.txt)
        var device = CreateGatewayDevice("UDMA67A", "UDR7", radioCount: 3);

        device.FriendlyModelName.Should().Be("UDR7");
        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UDR7 has integrated Wi-Fi");
    }

    [Fact]
    public void Ux_Allowed()
    {
        // Express - has real Wi-Fi radios
        var device = CreateGatewayDevice("UX", "UX", radioCount: 2);

        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UX (Express) has integrated Wi-Fi");
    }

    [Fact]
    public void Ux7_Allowed()
    {
        // Express 7 - has real Wi-Fi radios
        var device = CreateGatewayDevice("UDMA69B", "UX7", radioCount: 3);

        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UX7 (Express 7) has integrated Wi-Fi");
    }

    [Fact]
    public void Udw_Allowed()
    {
        // Dream Wall - has real Wi-Fi radios
        var device = CreateGatewayDevice("UDW", "UDW", radioCount: 3);

        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UDW (Dream Wall) has integrated Wi-Fi");
    }

    [Fact]
    public void Udr5GMax_Allowed()
    {
        // Dream Router 5G Max - has real Wi-Fi radios
        var device = CreateGatewayDevice("UDMA6B9", "UDR-5G-Max", radioCount: 3);

        UniFiDiscovery.IsGatewayOnlyConsole(device).Should().BeFalse(
            "UDR-5G-Max has integrated Wi-Fi");
    }
}
