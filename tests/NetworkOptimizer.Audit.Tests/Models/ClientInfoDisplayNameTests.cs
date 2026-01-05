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
