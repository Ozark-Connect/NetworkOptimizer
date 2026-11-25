using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.WiFi.Providers;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

/// <summary>Device type and private-MAC flags as the client mapper sets them.</summary>
public class ClientDeviceTypeMappingTests
{
    [Theory]
    [InlineData("fa:b1:00:11:22:33", true)]  // locally administered bit set
    [InlineData("1a:00:00:00:00:01", true)]
    [InlineData("00:11:22:33:44:55", false)] // maker's address
    [InlineData("4c:57:39:0a:15:f0", false)]
    [InlineData("", false)]
    public void Private_mac_is_the_locally_administered_bit(string mac, bool expected)
    {
        UniFiLiveDataProvider.IsLocallyAdministered(mac).Should().Be(expected);
    }

    [Fact]
    public void A_phone_on_a_private_mac_gets_the_mobile_advice()
    {
        // No fingerprint and no hostname, only the name UniFi Network shows: the private MAC decides.
        var client = new UniFiClientResponse { Mac = "fa:b1:00:11:22:33", ApMac = "aa:bb:cc:dd:ee:01", Radio = "na", Signal = -88 };
        var displayNames = new Dictionary<string, string> { ["fa:b1:00:11:22:33"] = "iPhone fa:b1" };

        var snapshot = UniFiLiveDataProvider.MapToWirelessClientSnapshot(
            client, new Dictionary<string, string>(), displayNames, DateTimeOffset.UnixEpoch);

        snapshot.IsPrivateMac.Should().BeTrue();
        NetworkOptimizer.WiFi.Analyzers.SiteHealthScorer
            .WeakSignalRecommendation(snapshot.DeviceCategory, snapshot.IsPrivateMac)
            .Should().StartWith("Check this device's Signal history");
    }

    [Fact]
    public void A_display_name_the_patterns_know_types_the_client()
    {
        // Name patterns are the Security Audit's: an IoT name types a device that has no hostname.
        var client = new UniFiClientResponse { Mac = "00:11:22:33:44:66", ApMac = "aa:bb:cc:dd:ee:01", Radio = "ng", Signal = -70 };
        var displayNames = new Dictionary<string, string> { ["00:11:22:33:44:66"] = "Kitchen Smart Plug" };

        var snapshot = UniFiLiveDataProvider.MapToWirelessClientSnapshot(
            client, new Dictionary<string, string>(), displayNames, DateTimeOffset.UnixEpoch);

        snapshot.DeviceCategory.Should().Be(ClientDeviceCategory.SmartPlug);
    }
}
