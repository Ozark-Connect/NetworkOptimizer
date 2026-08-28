using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ClientDashboardServiceTests
{
    [Fact]
    public void ResolveClientIp_UsesLastOrFixedAddressWhenCurrentAddressIsMissing()
    {
        ClientDashboardService.ResolveClientIp(new UniFiClientResponse
        {
            LastIp = "10.0.0.21",
            FixedIp = "10.0.0.20"
        }).Should().Be("10.0.0.21");

        ClientDashboardService.ResolveClientIp(new UniFiClientResponse
        {
            FixedIp = "10.0.0.20"
        }).Should().Be("10.0.0.20");
    }

    [Fact]
    public void ResolveClientIp_PrefersCurrentAddressOverFallbacks()
    {
        var client = new UniFiClientResponse
        {
            Ip = "10.0.0.22",
            LastIp = "10.0.0.21",
            FixedIp = "10.0.0.20"
        };

        ClientDashboardService.ResolveClientIp(client).Should().Be("10.0.0.22");
    }

    [Fact]
    public void ResolveClientIp_UsesV2LookupWhenStatStaHasNoAddress()
    {
        var client = new UniFiClientResponse { Mac = "44:a7:f4:32:28:e0" };
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["44:A7:F4:32:28:E0"] = "10.0.0.21"
        };

        ClientDashboardService.ResolveClientIp(client, lookup).Should().Be("10.0.0.21");
        ClientDashboardService.ClientMatchesIp(client, "10.0.0.21", lookup).Should().BeTrue();
    }

    [Fact]
    public void ResolveClientIp_DoesNotReplaceAStatStaAddressWithStaleV2Data()
    {
        var client = new UniFiClientResponse
        {
            Mac = "44:a7:f4:32:28:e0",
            Ip = "10.0.0.22"
        };
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [client.Mac] = "10.0.0.21"
        };

        ClientDashboardService.ResolveClientIp(client, lookup).Should().Be("10.0.0.22");
    }

    [Fact]
    public void ClientDetailMatchesIp_UsesV2BestAddress()
    {
        var client = new UniFiClientDetailResponse { LastIp = "10.0.0.3" };

        ClientDashboardService.ClientDetailMatchesIp(client, "10.0.0.3").Should().BeTrue();
    }

    [Fact]
    public void MapClientDetailToIdentity_PreservesMetadataAndRequestedAddress()
    {
        var client = new UniFiClientDetailResponse
        {
            Mac = "f4:4d:ad:05:58:36",
            DisplayName = "Work Mac",
            Hostname = "work-mac",
            Type = "WIRED",
            NetworkName = "Trusted",
            Oui = "Apple"
        };

        var identity = ClientDashboardService.MapClientDetailToIdentity(client, "fd00::1234");

        identity.Mac.Should().Be(client.Mac);
        identity.DisplayName.Should().Be("Work Mac");
        identity.Ip.Should().Be("fd00::1234");
        identity.IsWired.Should().BeTrue();
        identity.NetworkName.Should().Be("Trusted");
        identity.IsOffline.Should().BeFalse();
    }

    [Fact]
    public void TryGetMacFromNeighborOutput_MapsExactIpv6Neighbor()
    {
        var output = """
            fd00::1234 dev br2 lladdr f4:4d:ad:05:58:36 REACHABLE
            fd00::5678 dev br2 lladdr 44:a7:f4:32:28:e0 STALE
            """;

        ClientDashboardService.TryGetMacFromNeighborOutput(output, "fd00::1234")
            .Should().Be("f4:4d:ad:05:58:36");
    }

    [Theory]
    [InlineData("")]
    [InlineData("fd00::1234 dev br2 FAILED")]
    [InlineData("fd00::5678 dev br2 lladdr f4:4d:ad:05:58:36 REACHABLE")]
    public void TryGetMacFromNeighborOutput_RejectsMissingOrDifferentEntries(string output)
    {
        ClientDashboardService.TryGetMacFromNeighborOutput(output, "fd00::1234")
            .Should().BeNull();
    }

    [Fact]
    public void MacEquals_IsCaseInsensitive()
    {
        ClientDashboardService.MacEquals("F4:4D:AD:05:58:36", "f4:4d:ad:05:58:36")
            .Should().BeTrue();
    }
}
