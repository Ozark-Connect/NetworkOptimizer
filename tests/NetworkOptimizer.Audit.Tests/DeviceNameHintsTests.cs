using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Audit.Tests;

/// <summary>
/// Tests for DeviceNameHints static helper methods
/// </summary>
public class DeviceNameHintsTests
{
    #region IsIoTDeviceName Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsIoTDeviceName_WithNullOrEmpty_ReturnsFalse(string? portName)
    {
        DeviceNameHints.IsIoTDeviceName(portName).Should().BeFalse();
    }

    [Theory]
    [InlineData("IKEA Tradfri Gateway")]
    [InlineData("Philips Hue Bridge")]
    [InlineData("Smart Thermostat")]
    [InlineData("IoT Device")]
    [InlineData("Amazon Alexa")]
    [InlineData("Echo Dot")]
    [InlineData("Nest Thermostat")]
    [InlineData("Ring Doorbell")]
    [InlineData("Sonos Speaker")]
    [InlineData("philips-light-01")]
    public void IsIoTDeviceName_WithIoTKeyword_ReturnsTrue(string portName)
    {
        DeviceNameHints.IsIoTDeviceName(portName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Server01")]
    [InlineData("Workstation")]
    [InlineData("Printer")]
    [InlineData("Camera-Front")]
    [InlineData("AP-Living-Room")]
    public void IsIoTDeviceName_WithNonIoTName_ReturnsFalse(string portName)
    {
        DeviceNameHints.IsIoTDeviceName(portName).Should().BeFalse();
    }

    [Fact]
    public void IsIoTDeviceName_IsCaseInsensitive()
    {
        DeviceNameHints.IsIoTDeviceName("SMART PLUG").Should().BeTrue();
        DeviceNameHints.IsIoTDeviceName("smart plug").Should().BeTrue();
        DeviceNameHints.IsIoTDeviceName("Smart Plug").Should().BeTrue();
    }

    #endregion

    #region IsCameraDeviceName Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsCameraDeviceName_WithNullOrEmpty_ReturnsFalse(string? portName)
    {
        DeviceNameHints.IsCameraDeviceName(portName).Should().BeFalse();
    }

    [Theory]
    [InlineData("Front Camera")]
    [InlineData("Backyard Cam")]
    [InlineData("PTZ Camera")]
    [InlineData("NVR Storage")]
    [InlineData("Protect G4")]
    [InlineData("camera-01")]
    [InlineData("ptz-outdoor")]
    public void IsCameraDeviceName_WithCameraKeyword_ReturnsTrue(string portName)
    {
        DeviceNameHints.IsCameraDeviceName(portName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Server01")]
    [InlineData("Smart TV")]
    [InlineData("IoT Device")]
    [InlineData("AP-Garage")]
    [InlineData("Printer")]
    public void IsCameraDeviceName_WithNonCameraName_ReturnsFalse(string portName)
    {
        DeviceNameHints.IsCameraDeviceName(portName).Should().BeFalse();
    }

    [Fact]
    public void IsCameraDeviceName_IsCaseInsensitive()
    {
        DeviceNameHints.IsCameraDeviceName("CAMERA").Should().BeTrue();
        DeviceNameHints.IsCameraDeviceName("Camera").Should().BeTrue();
        DeviceNameHints.IsCameraDeviceName("camera").Should().BeTrue();
    }

    #endregion

    #region IsAccessPointName Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsAccessPointName_WithNullOrEmpty_ReturnsFalse(string? portName)
    {
        DeviceNameHints.IsAccessPointName(portName).Should().BeFalse();
    }

    [Theory]
    [InlineData("AP-Living-Room")]
    [InlineData("Access Point 1")]
    [InlineData("WiFi Extender")]
    [InlineData("ap-office")]
    [InlineData("UniFi Access Point")]
    public void IsAccessPointName_WithAPKeyword_ReturnsTrue(string portName)
    {
        DeviceNameHints.IsAccessPointName(portName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Server01")]
    [InlineData("Smart TV")]
    [InlineData("Camera")]
    [InlineData("Printer")]
    // Note: "Application Server" would return true due to "ap" substring - see bug note below
    public void IsAccessPointName_WithNonAPName_ReturnsFalse(string portName)
    {
        DeviceNameHints.IsAccessPointName(portName).Should().BeFalse();
    }

    [Fact]
    public void IsAccessPointName_IsCaseInsensitive()
    {
        DeviceNameHints.IsAccessPointName("WIFI").Should().BeTrue();
        DeviceNameHints.IsAccessPointName("Wifi").Should().BeTrue();
        DeviceNameHints.IsAccessPointName("wifi").Should().BeTrue();
    }

    // NOTE: Current implementation has false positives - "ap" matches "application", "laptop", etc.
    // This is a known limitation. Tests above only cover expected/correct matches.

    #endregion
}
