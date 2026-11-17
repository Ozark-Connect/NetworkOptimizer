using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class ClientNamesTests
{
    [Fact]
    public void OrdinaryClient_WithNoAlias_TakesUniFisDisplayName_NotTheDhcpHostname()
    {
        // UniFi's fingerprint name for an unnamed phone beats the DHCP hostname.
        ClientNames.Choose("iPhone 24:ae", null, null, "Users-iPhone", isUniFiDevice: false).Should().Be("iPhone 24:ae");
    }

    [Fact]
    public void OrdinaryClient_KeepsDisplayNameThenAliasThenHostname()
    {
        ClientNames.Choose("Laptop", "Laptop", null, "laptop-01", isUniFiDevice: false).Should().Be("Laptop");
        ClientNames.Choose(null, "Laptop", null, "laptop-01", isUniFiDevice: false).Should().Be("Laptop");
        ClientNames.Choose(null, null, null, "laptop-01", isUniFiDevice: false).Should().Be("laptop-01");
        ClientNames.Choose(null, null, null, "laptop-01", isUniFiDevice: false, hostnameFallback: false).Should().BeNull();
    }

    [Fact]
    public void UniFiDevice_RenamedInItsApp_BeatsAStaleAlias()
    {
        ClientNames.Choose("[Cam] Casita - Front Door", "[Cam] Tiny Home - Front Door", "[Cam] Casita - Front Door",
            "cam-casita---front-door", isUniFiDevice: true).Should().Be("[Cam] Casita - Front Door");
    }

    [Fact]
    public void UniFiDevice_WhoseAppNameArrivedInHostnameForm_KeepsTheAlias()
    {
        // UniFi OS hands "[Security] AI Key" to Network as "SecurityAIKey", the device's hostname.
        ClientNames.Choose("SecurityAIKey", "[Security] AI Key", "SecurityAIKey", "SecurityAIKey", isUniFiDevice: true)
            .Should().Be("[Security] AI Key");
        ClientNames.Choose("SecurityAIKey", null, "SecurityAIKey", "SecurityAIKey", isUniFiDevice: true)
            .Should().Be("SecurityAIKey", "with no alias the app's name is still the best there is");
    }

    [Fact]
    public void V2Client_UsesUnifiDeviceToApplyTheDeviceRule()
    {
        var aiKey = new Models.UniFiClientDetailResponse
        {
            Mac = "aa:bb:cc:dd:ee:01", Name = "[Security] AI Key", DisplayName = "SecurityAIKey", Hostname = "SecurityAIKey", UnifiDevice = true,
        };
        var phone = new Models.UniFiClientDetailResponse { Mac = "aa:bb:cc:dd:ee:02", DisplayName = "iPhone 24:ae", Hostname = "Users-iPhone" };

        ClientNames.For(aiKey).Should().Be("[Security] AI Key");
        ClientNames.For(phone).Should().Be("iPhone 24:ae");
    }

    [Fact]
    public void UniFiDevice_OfflineWithNoAppName_FallsBackToTheAlias()
    {
        ClientNames.Choose(null, "[Cam] Chicken Coop", null, "cam-dogs", isUniFiDevice: true).Should().Be("[Cam] Chicken Coop");
    }
}
