using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Models;

/// <summary>
/// Tests for DisplayName fallback logic in WirelessClientInfo and OfflineClientInfo
/// </summary>
public class ClientInfoDisplayNameTests
{
    #region WirelessClientInfo.DisplayName Tests

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsName_WhenNameIsSet()
    {
        var client = CreateWirelessClient(name: "My iPhone", hostname: "iphone-12", mac: "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, productName: "iPhone 14 Pro", category: ClientDeviceCategory.Smartphone);

        info.DisplayName.Should().Be("My iPhone");
    }

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsHostname_WhenNameIsEmpty()
    {
        var client = CreateWirelessClient(name: "", hostname: "iphone-12", mac: "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, productName: "iPhone 14 Pro", category: ClientDeviceCategory.Smartphone);

        info.DisplayName.Should().Be("iphone-12");
    }

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsHostname_WhenNameIsWhitespace()
    {
        var client = CreateWirelessClient(name: "   ", hostname: "iphone-12", mac: "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, productName: "iPhone 14 Pro", category: ClientDeviceCategory.Smartphone);

        info.DisplayName.Should().Be("iphone-12");
    }

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsProductName_WhenNameAndHostnameEmpty()
    {
        var client = CreateWirelessClient(name: "", hostname: "", mac: "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, productName: "iPhone 14 Pro", category: ClientDeviceCategory.Smartphone);

        info.DisplayName.Should().Be("iPhone 14 Pro");
    }

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsCategoryName_WhenProductNameEmpty()
    {
        var client = CreateWirelessClient(name: "", hostname: "", mac: "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, productName: null, category: ClientDeviceCategory.Smartphone);

        info.DisplayName.Should().Be("Smartphone");
    }

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsMac_WhenCategoryIsUnknown()
    {
        var client = CreateWirelessClient(name: "", hostname: "", mac: "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, productName: null, category: ClientDeviceCategory.Unknown);

        info.DisplayName.Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void WirelessClientInfo_DisplayName_ReturnsUnknown_WhenAllFieldsEmpty()
    {
        var client = CreateWirelessClient(name: "", hostname: "", mac: "");
        var info = CreateWirelessClientInfo(client, productName: null, category: ClientDeviceCategory.Unknown);

        info.DisplayName.Should().Be("Unknown");
    }

    #endregion

    #region OfflineClientInfo.DisplayName Tests

    [Fact]
    public void OfflineClientInfo_DisplayName_ReturnsDisplayName_WhenSet()
    {
        var history = CreateHistoryClient(displayName: "Living Room TV", name: "tv-samsung", hostname: "samsung-tv", mac: "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, productName: "Samsung Smart TV", category: ClientDeviceCategory.SmartTV);

        info.DisplayName.Should().Be("Living Room TV");
    }

    [Fact]
    public void OfflineClientInfo_DisplayName_ReturnsName_WhenDisplayNameEmpty()
    {
        var history = CreateHistoryClient(displayName: "", name: "tv-samsung", hostname: "samsung-tv", mac: "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, productName: "Samsung Smart TV", category: ClientDeviceCategory.SmartTV);

        info.DisplayName.Should().Be("tv-samsung");
    }

    [Fact]
    public void OfflineClientInfo_DisplayName_ReturnsHostname_WhenNameEmpty()
    {
        var history = CreateHistoryClient(displayName: "", name: "", hostname: "samsung-tv", mac: "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, productName: "Samsung Smart TV", category: ClientDeviceCategory.SmartTV);

        info.DisplayName.Should().Be("samsung-tv");
    }

    [Fact]
    public void OfflineClientInfo_DisplayName_ReturnsProductName_WhenHostnameEmpty()
    {
        var history = CreateHistoryClient(displayName: "", name: "", hostname: "", mac: "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, productName: "Samsung Smart TV", category: ClientDeviceCategory.SmartTV);

        info.DisplayName.Should().Be("Samsung Smart TV");
    }

    [Fact]
    public void OfflineClientInfo_DisplayName_ReturnsCategoryName_WhenProductNameEmpty()
    {
        var history = CreateHistoryClient(displayName: "", name: "", hostname: "", mac: "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, productName: null, category: ClientDeviceCategory.SmartTV);

        info.DisplayName.Should().Be("Smart TV");
    }

    [Fact]
    public void OfflineClientInfo_DisplayName_ReturnsMac_WhenCategoryUnknown()
    {
        var history = CreateHistoryClient(displayName: "", name: "", hostname: "", mac: "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, productName: null, category: ClientDeviceCategory.Unknown);

        info.DisplayName.Should().Be("11:22:33:44:55:66");
    }

    #endregion

    #region WirelessClientInfo.WifiBand Tests

    [Theory]
    [InlineData("na", "5 GHz")]
    [InlineData("ng", "2.4 GHz")]
    [InlineData("6e", "6 GHz")]
    [InlineData("ax-6e", "6 GHz")]
    [InlineData("NA", "5 GHz")]  // Case insensitive
    [InlineData("NG", "2.4 GHz")]
    public void WirelessClientInfo_WifiBand_ReturnsCorrectBand_FromRadioType(string radio, string expectedBand)
    {
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Radio = radio,
            IsWired = false
        };
        var info = CreateWirelessClientInfo(client, null, ClientDeviceCategory.Unknown);

        info.WifiBand.Should().Be(expectedBand);
    }

    [Theory]
    [InlineData(1, "2.4 GHz")]
    [InlineData(6, "2.4 GHz")]
    [InlineData(11, "2.4 GHz")]
    [InlineData(14, "2.4 GHz")]
    [InlineData(36, "5 GHz")]
    [InlineData(44, "5 GHz")]
    [InlineData(149, "5 GHz")]
    [InlineData(177, "5 GHz")]
    [InlineData(181, "6 GHz")]
    [InlineData(233, "6 GHz")]
    public void WirelessClientInfo_WifiBand_ReturnsCorrectBand_FromChannel(int channel, string expectedBand)
    {
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Radio = null,
            Channel = channel,
            IsWired = false
        };
        var info = CreateWirelessClientInfo(client, null, ClientDeviceCategory.Unknown);

        info.WifiBand.Should().Be(expectedBand);
    }

    [Fact]
    public void WirelessClientInfo_WifiBand_ReturnsNull_WhenNoRadioOrChannel()
    {
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Radio = null,
            Channel = null,
            IsWired = false
        };
        var info = CreateWirelessClientInfo(client, null, ClientDeviceCategory.Unknown);

        info.WifiBand.Should().BeNull();
    }

    [Fact]
    public void WirelessClientInfo_WifiBand_PrefersRadioType_OverChannel()
    {
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Radio = "na",  // 5 GHz
            Channel = 6,   // Would indicate 2.4 GHz if radio wasn't set
            IsWired = false
        };
        var info = CreateWirelessClientInfo(client, null, ClientDeviceCategory.Unknown);

        info.WifiBand.Should().Be("5 GHz");
    }

    [Fact]
    public void WirelessClientInfo_WifiBand_FallsBackToChannel_WhenRadioUnrecognized()
    {
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:ff",
            Radio = "unknown-radio",
            Channel = 36,  // 5 GHz
            IsWired = false
        };
        var info = CreateWirelessClientInfo(client, null, ClientDeviceCategory.Unknown);

        info.WifiBand.Should().Be("5 GHz");
    }

    [Fact]
    public void WirelessClientInfo_Mac_ReturnsClientMac()
    {
        var client = CreateWirelessClient("Test", "", "aa:bb:cc:dd:ee:ff");
        var info = CreateWirelessClientInfo(client, null, ClientDeviceCategory.Unknown);

        info.Mac.Should().Be("aa:bb:cc:dd:ee:ff");
    }

    #endregion

    #region OfflineClientInfo Property Tests

    [Fact]
    public void OfflineClientInfo_LastSeenDisplay_ReturnsMinutes_WhenUnderOneHour()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.LastSeenDisplay.Should().Be("30 min ago");
    }

    [Fact]
    public void OfflineClientInfo_LastSeenDisplay_ReturnsHours_WhenUnderOneDay()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddHours(-5).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.LastSeenDisplay.Should().Be("5 hr ago");
    }

    [Fact]
    public void OfflineClientInfo_LastSeenDisplay_ReturnsDays_WhenUnderOneWeek()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.LastSeenDisplay.Should().Be("3 days ago");
    }

    [Fact]
    public void OfflineClientInfo_LastSeenDisplay_ReturnsWeeks_WhenOverOneWeek()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddDays(-21).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.LastSeenDisplay.Should().Be("3 weeks ago");
    }

    [Fact]
    public void OfflineClientInfo_IsRecentlyActive_ReturnsTrue_WhenWithin14Days()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.IsRecentlyActive.Should().BeTrue();
    }

    [Fact]
    public void OfflineClientInfo_IsRecentlyActive_ReturnsFalse_WhenOlderThan14Days()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddDays(-15).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.IsRecentlyActive.Should().BeFalse();
    }

    [Fact]
    public void OfflineClientInfo_IsRecentlyActive_ReturnsTrueAtJustUnder14Days()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = DateTimeOffset.UtcNow.AddDays(-13).AddHours(-23).ToUnixTimeSeconds();
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.IsRecentlyActive.Should().BeTrue();
    }

    [Fact]
    public void OfflineClientInfo_Mac_ReturnsHistoryClientMac()
    {
        var history = CreateHistoryClient("", "", "", "11:22:33:44:55:66");
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.Mac.Should().Be("11:22:33:44:55:66");
    }

    [Fact]
    public void OfflineClientInfo_IsWired_ReturnsHistoryClientIsWired()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.IsWired = true;
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.IsWired.Should().BeTrue();
    }

    [Fact]
    public void OfflineClientInfo_LastSeenDateTime_ConvertsUnixTimestampCorrectly()
    {
        var expectedTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var unixTime = new DateTimeOffset(expectedTime).ToUnixTimeSeconds();
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastSeen = unixTime;
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.LastSeenDateTime.Should().Be(expectedTime);
    }

    [Fact]
    public void OfflineClientInfo_LastUplinkName_ReturnsHistoryClientLastUplinkName()
    {
        var history = CreateHistoryClient("", "", "", "aa:bb:cc:dd:ee:ff");
        history.LastUplinkName = "AP-Living-Room";
        var info = CreateOfflineClientInfo(history, null, ClientDeviceCategory.Unknown);

        info.LastUplinkName.Should().Be("AP-Living-Room");
    }

    #endregion

    #region Helper Methods

    private static UniFiClientResponse CreateWirelessClient(string name, string hostname, string? mac)
    {
        return new UniFiClientResponse
        {
            Name = name,
            Hostname = hostname,
            Mac = mac ?? string.Empty,
            IsWired = false
        };
    }

    private static WirelessClientInfo CreateWirelessClientInfo(
        UniFiClientResponse client,
        string? productName,
        ClientDeviceCategory category)
    {
        return new WirelessClientInfo
        {
            Client = client,
            Detection = new DeviceDetectionResult
            {
                Category = category,
                ProductName = productName,
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 80,
                RecommendedNetwork = NetworkPurpose.Corporate
            }
        };
    }

    private static UniFiClientHistoryResponse CreateHistoryClient(
        string displayName,
        string name,
        string hostname,
        string mac)
    {
        return new UniFiClientHistoryResponse
        {
            DisplayName = displayName,
            Name = name,
            Hostname = hostname,
            Mac = mac,
            LastSeen = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private static OfflineClientInfo CreateOfflineClientInfo(
        UniFiClientHistoryResponse historyClient,
        string? productName,
        ClientDeviceCategory category)
    {
        return new OfflineClientInfo
        {
            HistoryClient = historyClient,
            Detection = new DeviceDetectionResult
            {
                Category = category,
                ProductName = productName,
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 80,
                RecommendedNetwork = NetworkPurpose.Corporate
            }
        };
    }

    #endregion
}
