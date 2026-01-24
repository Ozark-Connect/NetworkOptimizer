using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
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

    #region Additional OUI Coverage

    [Theory]
    [InlineData("00:11:32:11:22:33", ClientDeviceCategory.NAS, "Synology")]
    [InlineData("00:08:9B:11:22:33", ClientDeviceCategory.NAS, "QNAP")]
    [InlineData("50:14:79:11:22:33", ClientDeviceCategory.RoboticVacuum, "iRobot")]
    [InlineData("50:EC:50:11:22:33", ClientDeviceCategory.RoboticVacuum, "Roborock")]
    [InlineData("D8:6C:63:11:22:33", ClientDeviceCategory.SmartLock, "August")]
    [InlineData("28:6D:97:11:22:33", ClientDeviceCategory.SmartHub, "Samsung SmartThings")]
    [InlineData("24:F5:A2:11:22:33", ClientDeviceCategory.SmartPlug, "Wemo")]
    [InlineData("48:E1:E9:11:22:33", ClientDeviceCategory.SmartPlug, "Meross")]
    [InlineData("44:61:32:11:22:33", ClientDeviceCategory.SmartThermostat, "Ecobee")]
    [InlineData("00:D0:2D:11:22:33", ClientDeviceCategory.SmartThermostat, "Honeywell")]
    [InlineData("78:11:DC:11:22:33", ClientDeviceCategory.RoboticVacuum, "Roborock")]
    [InlineData("C8:95:2C:11:22:33", ClientDeviceCategory.RoboticVacuum, "Ecovacs")]
    [InlineData("00:17:C9:11:22:33", ClientDeviceCategory.SmartLock, "Yale")]
    [InlineData("00:1A:22:11:22:33", ClientDeviceCategory.SmartLock, "Schlage")]
    [InlineData("8C:85:80:11:22:33", ClientDeviceCategory.Camera, "Eufy")]
    [InlineData("AC:0B:FB:11:22:33", ClientDeviceCategory.RoboticVacuum, "Eufy")]
    [InlineData("D0:73:D5:11:22:33", ClientDeviceCategory.SmartLighting, "LIFX")]
    [InlineData("00:0D:5C:11:22:33", ClientDeviceCategory.SmartLighting, "Lutron")]
    [InlineData("94:54:93:11:22:33", ClientDeviceCategory.SmartLighting, "IKEA")]
    public void Detect_AdditionalIotDevices_ReturnsCorrectCategory(string mac, ClientDeviceCategory expectedCategory, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(expectedCategory);
        result.VendorName.Should().Contain(expectedVendor);
    }

    #endregion

    #region Additional Game Consoles and Streaming

    [Theory]
    [InlineData("00:0D:3A:11:22:33", ClientDeviceCategory.GameConsole, "Xbox")]
    [InlineData("00:1F:32:11:22:33", ClientDeviceCategory.GameConsole, "Nintendo")]
    [InlineData("40:CB:C0:11:22:33", ClientDeviceCategory.StreamingDevice, "Apple TV")]
    [InlineData("54:60:09:11:22:33", ClientDeviceCategory.StreamingDevice, "Chromecast")]
    [InlineData("4C:EF:C0:11:22:33", ClientDeviceCategory.StreamingDevice, "Amazon Fire")]
    public void Detect_GameConsolesAndStreaming_ReturnsCorrectCategory(string mac, ClientDeviceCategory expectedCategory, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(expectedCategory);
        result.VendorName.Should().Contain(expectedVendor);
    }

    #endregion

    #region Additional Ring/Amazon OUIs

    [Theory]
    [InlineData("44:73:D6:11:22:33", ClientDeviceCategory.CloudCamera, "Ring")]
    [InlineData("A4:DA:22:11:22:33", ClientDeviceCategory.CloudCamera, "Ring")]
    [InlineData("90:48:9A:11:22:33", ClientDeviceCategory.CloudCamera, "Ring")]
    [InlineData("FC:65:DE:11:22:33", ClientDeviceCategory.SmartSpeaker, "Amazon Echo")]
    [InlineData("68:54:FD:11:22:33", ClientDeviceCategory.SmartSpeaker, "Amazon Echo")]
    public void Detect_RingAndAmazonOuis_ReturnsCorrectCategory(string mac, ClientDeviceCategory expectedCategory, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(expectedCategory);
        result.VendorName.Should().Contain(expectedVendor);
    }

    #endregion

    #region Additional Sonos OUIs

    [Theory]
    [InlineData("5C:AA:FD:11:22:33", "Sonos")]
    [InlineData("94:9F:3E:11:22:33", "Sonos")]
    [InlineData("78:28:CA:11:22:33", "Sonos")]
    [InlineData("B8:E9:37:11:22:33", "Sonos")]
    [InlineData("54:2A:1B:11:22:33", "Sonos")]
    [InlineData("34:7E:5C:11:22:33", "Sonos")]
    public void Detect_SonosOuis_ReturnsMediaPlayer(string mac, string expectedVendor)
    {
        var result = _detector.Detect(mac);

        result.Category.Should().Be(ClientDeviceCategory.MediaPlayer);
        result.VendorName.Should().Contain(expectedVendor);
    }

    #endregion
}
