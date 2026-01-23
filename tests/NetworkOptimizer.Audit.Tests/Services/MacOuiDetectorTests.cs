using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services.Detectors;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Services;

public class MacOuiDetectorTests
{
    private readonly MacOuiDetector _detector = new();

    #region Detect - Null/Empty Input

    [Fact]
    public void Detect_NullMac_ReturnsUnknown()
    {
        var result = _detector.Detect(null!);
        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    [Fact]
    public void Detect_EmptyMac_ReturnsUnknown()
    {
        var result = _detector.Detect(string.Empty);
        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    #endregion

    #region Detect - Curated OUI Mappings

    [Theory]
    [InlineData("0C:47:C9:11:22:33", ClientDeviceCategory.CloudCamera, "Ring")]
    [InlineData("0c:47:c9:aa:bb:cc", ClientDeviceCategory.CloudCamera, "Ring")] // lowercase
    [InlineData("00:17:88:11:22:33", ClientDeviceCategory.SmartLighting, "Philips Hue")]
    [InlineData("08:05:81:11:22:33", ClientDeviceCategory.StreamingDevice, "Roku")]
    [InlineData("00:0E:58:11:22:33", ClientDeviceCategory.MediaPlayer, "Sonos")]
    [InlineData("84:D6:D0:11:22:33", ClientDeviceCategory.SmartSpeaker, "Amazon Echo")]
    [InlineData("00:04:1F:11:22:33", ClientDeviceCategory.GameConsole, "Sony PlayStation")]
    [InlineData("18:B4:30:11:22:33", ClientDeviceCategory.SmartThermostat, "Nest")]
    [InlineData("EC:71:DB:11:22:33", ClientDeviceCategory.Camera, "Reolink")]
    [InlineData("FC:EC:DA:11:22:33", ClientDeviceCategory.Camera, "UniFi Protect")]
    public void Detect_CuratedOui_ReturnsExpectedCategory(string mac, ClientDeviceCategory expectedCategory, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(expectedCategory);
        result.VendorName.Should().Contain(expectedVendor);
        result.Source.Should().Be(DetectionSource.MacOui);
        result.ConfidenceScore.Should().BeGreaterThan(0);
    }

    #endregion

    #region Detect - MAC Format Normalization

    [Fact]
    public void Detect_MacWithDashes_NormalizesCorrectly()
    {
        // Ring MAC with dashes instead of colons
        var result = _detector.Detect("0C-47-C9-11-22-33");

        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Ring");
    }

    [Fact]
    public void Detect_MacWithDots_NormalizesCorrectly()
    {
        // Ring MAC with dots (Cisco format)
        var result = _detector.Detect("0C47.C911.2233");

        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Ring");
    }

    [Fact]
    public void Detect_MacWithNoSeparators_NormalizesCorrectly()
    {
        // Ring MAC with no separators
        var result = _detector.Detect("0C47C9112233");

        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Be("Ring");
    }

    [Fact]
    public void Detect_ShortMac_ReturnsUnknown()
    {
        // MAC too short to extract OUI
        var result = _detector.Detect("0C47");

        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    #endregion

    #region Detect - Unknown OUI

    [Fact]
    public void Detect_UnknownOui_ReturnsUnknown()
    {
        // Random MAC that's not in any mapping
        var result = _detector.Detect("AA:BB:CC:11:22:33");

        result.Category.Should().Be(ClientDeviceCategory.Unknown);
    }

    #endregion

    #region Detect - Metadata

    [Fact]
    public void Detect_CuratedOui_IncludesMetadata()
    {
        var result = _detector.Detect("00:17:88:11:22:33");

        result.Metadata.Should().ContainKey("oui");
        result.Metadata.Should().ContainKey("vendor");
        result.Metadata!["oui"].Should().Be("00:17:88");
        result.Metadata["vendor"].Should().Be("Philips Hue");
    }

    #endregion

    #region Detect - All Cloud Camera OUIs

    [Theory]
    [InlineData("0C:47:C9:00:00:00", "Ring")]
    [InlineData("34:1F:4F:00:00:00", "Ring")]
    [InlineData("2C:AA:8E:00:00:00", "Wyze")]
    [InlineData("9C:55:B4:00:00:00", "Blink")]
    [InlineData("4C:77:6D:00:00:00", "Arlo")]
    public void Detect_CloudCameraOui_ReturnsCloudCamera(string mac, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(ClientDeviceCategory.CloudCamera);
        result.VendorName.Should().Contain(expectedVendor);
    }

    #endregion

    #region Detect - Self-Hosted Camera OUIs

    [Theory]
    [InlineData("FC:EC:DA:00:00:00", "UniFi Protect")]
    [InlineData("EC:71:DB:00:00:00", "Reolink")]
    [InlineData("C4:2F:90:00:00:00", "Hikvision")]
    [InlineData("3C:EF:8C:00:00:00", "Dahua")]
    public void Detect_SelfHostedCameraOui_ReturnsCamera(string mac, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(ClientDeviceCategory.Camera);
        result.VendorName.Should().Contain(expectedVendor);
    }

    #endregion
}
