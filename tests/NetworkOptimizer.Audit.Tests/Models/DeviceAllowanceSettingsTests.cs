using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Models;

/// <summary>
/// Tests for DeviceAllowanceSettings - validates device-specific allowance logic
/// </summary>
public class DeviceAllowanceSettingsTests
{
    #region IsStreamingDeviceAllowed Tests

    [Fact]
    public void IsStreamingDeviceAllowed_AllowAll_ReturnsTrue_ForAnyVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAllStreamingOnMainNetwork = true };

        settings.IsStreamingDeviceAllowed("Apple").Should().BeTrue();
        settings.IsStreamingDeviceAllowed("Roku").Should().BeTrue();
        settings.IsStreamingDeviceAllowed("Amazon").Should().BeTrue();
        settings.IsStreamingDeviceAllowed(null).Should().BeTrue();
    }

    [Fact]
    public void IsStreamingDeviceAllowed_AllowAppleOnly_ReturnsTrue_ForAppleVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsStreamingDeviceAllowed("Apple").Should().BeTrue();
        settings.IsStreamingDeviceAllowed("Apple Inc").Should().BeTrue();
        settings.IsStreamingDeviceAllowed("apple tv").Should().BeTrue();
    }

    [Fact]
    public void IsStreamingDeviceAllowed_AllowAppleOnly_ReturnsFalse_ForNonAppleVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsStreamingDeviceAllowed("Roku").Should().BeFalse();
        settings.IsStreamingDeviceAllowed("Amazon").Should().BeFalse();
        settings.IsStreamingDeviceAllowed("Google").Should().BeFalse();
    }

    [Fact]
    public void IsStreamingDeviceAllowed_AllowAppleOnly_ReturnsFalse_ForNullVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsStreamingDeviceAllowed(null).Should().BeFalse();
        settings.IsStreamingDeviceAllowed("").Should().BeFalse();
    }

    [Fact]
    public void IsStreamingDeviceAllowed_NoAllowances_ReturnsFalse()
    {
        var settings = new DeviceAllowanceSettings();

        settings.IsStreamingDeviceAllowed("Apple").Should().BeFalse();
        settings.IsStreamingDeviceAllowed("Roku").Should().BeFalse();
    }

    [Fact]
    public void IsStreamingDeviceAllowed_AllowAll_TakesPrecedenceOverAppleOnly()
    {
        var settings = new DeviceAllowanceSettings
        {
            AllowAllStreamingOnMainNetwork = true,
            AllowAppleStreamingOnMainNetwork = false
        };

        settings.IsStreamingDeviceAllowed("Roku").Should().BeTrue();
    }

    #endregion

    #region IsSmartTVAllowed Tests

    [Fact]
    public void IsSmartTVAllowed_AllowAll_ReturnsTrue_ForAnyVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAllTVsOnMainNetwork = true };

        settings.IsSmartTVAllowed("LG").Should().BeTrue();
        settings.IsSmartTVAllowed("Samsung").Should().BeTrue();
        settings.IsSmartTVAllowed("TCL").Should().BeTrue();
        settings.IsSmartTVAllowed("Vizio").Should().BeTrue();
        settings.IsSmartTVAllowed(null).Should().BeTrue();
    }

    [Fact]
    public void IsSmartTVAllowed_AllowNameBrand_ReturnsTrue_ForNameBrandVendors()
    {
        var settings = new DeviceAllowanceSettings { AllowNameBrandTVsOnMainNetwork = true };

        settings.IsSmartTVAllowed("LG Electronics").Should().BeTrue();
        settings.IsSmartTVAllowed("Samsung").Should().BeTrue();
        settings.IsSmartTVAllowed("Sony Corporation").Should().BeTrue();
    }

    [Theory]
    [InlineData("lg")]
    [InlineData("LG")]
    [InlineData("LG Electronics")]
    [InlineData("samsung")]
    [InlineData("SAMSUNG")]
    [InlineData("Samsung Electronics")]
    [InlineData("sony")]
    [InlineData("SONY")]
    [InlineData("Sony Corporation")]
    public void IsSmartTVAllowed_AllowNameBrand_CaseInsensitive(string vendor)
    {
        var settings = new DeviceAllowanceSettings { AllowNameBrandTVsOnMainNetwork = true };

        settings.IsSmartTVAllowed(vendor).Should().BeTrue();
    }

    [Fact]
    public void IsSmartTVAllowed_AllowNameBrand_ReturnsFalse_ForOffBrandVendors()
    {
        var settings = new DeviceAllowanceSettings { AllowNameBrandTVsOnMainNetwork = true };

        settings.IsSmartTVAllowed("TCL").Should().BeFalse();
        settings.IsSmartTVAllowed("Vizio").Should().BeFalse();
        settings.IsSmartTVAllowed("Hisense").Should().BeFalse();
        settings.IsSmartTVAllowed("Insignia").Should().BeFalse();
    }

    [Fact]
    public void IsSmartTVAllowed_AllowNameBrand_ReturnsFalse_ForNullVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowNameBrandTVsOnMainNetwork = true };

        settings.IsSmartTVAllowed(null).Should().BeFalse();
        settings.IsSmartTVAllowed("").Should().BeFalse();
    }

    [Fact]
    public void IsSmartTVAllowed_NoAllowances_ReturnsFalse()
    {
        var settings = new DeviceAllowanceSettings();

        settings.IsSmartTVAllowed("LG").Should().BeFalse();
        settings.IsSmartTVAllowed("Samsung").Should().BeFalse();
        settings.IsSmartTVAllowed("Sony").Should().BeFalse();
    }

    [Fact]
    public void IsSmartTVAllowed_AllowAppleStreaming_ReturnsTrue_ForAppleVendor()
    {
        // Apple TV is categorized as SmartTV by UniFi (dev_type_id=47)
        // so the Apple streaming allowance should apply to SmartTV too
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsSmartTVAllowed("Apple").Should().BeTrue();
        settings.IsSmartTVAllowed("Apple Inc").Should().BeTrue();
        settings.IsSmartTVAllowed("apple").Should().BeTrue();
    }

    [Fact]
    public void IsSmartTVAllowed_AllowAppleStreaming_ReturnsFalse_ForNonAppleVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsSmartTVAllowed("LG").Should().BeFalse();
        settings.IsSmartTVAllowed("Samsung").Should().BeFalse();
        settings.IsSmartTVAllowed("TCL").Should().BeFalse();
    }

    [Fact]
    public void IsSmartTVAllowed_AllowAll_TakesPrecedenceOverNameBrand()
    {
        var settings = new DeviceAllowanceSettings
        {
            AllowAllTVsOnMainNetwork = true,
            AllowNameBrandTVsOnMainNetwork = false
        };

        settings.IsSmartTVAllowed("TCL").Should().BeTrue();
        settings.IsSmartTVAllowed("Hisense").Should().BeTrue();
    }

    #endregion

    #region IsSmartSpeakerAllowed Tests

    [Fact]
    public void IsSmartSpeakerAllowed_AllowAppleStreaming_ReturnsTrue_ForAppleVendor()
    {
        // Apple HomePod is categorized as SmartSpeaker
        // so the Apple streaming allowance should apply
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsSmartSpeakerAllowed("Apple").Should().BeTrue();
        settings.IsSmartSpeakerAllowed("Apple Inc").Should().BeTrue();
        settings.IsSmartSpeakerAllowed("apple").Should().BeTrue();
    }

    [Fact]
    public void IsSmartSpeakerAllowed_AllowAppleStreaming_ReturnsFalse_ForNonAppleVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsSmartSpeakerAllowed("Amazon").Should().BeFalse();
        settings.IsSmartSpeakerAllowed("Google").Should().BeFalse();
        settings.IsSmartSpeakerAllowed("Sonos").Should().BeFalse();
    }

    [Fact]
    public void IsSmartSpeakerAllowed_AllowAppleStreaming_ReturnsFalse_ForNullVendor()
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsSmartSpeakerAllowed(null).Should().BeFalse();
        settings.IsSmartSpeakerAllowed("").Should().BeFalse();
    }

    [Fact]
    public void IsSmartSpeakerAllowed_NoAllowances_ReturnsFalse()
    {
        var settings = new DeviceAllowanceSettings();

        settings.IsSmartSpeakerAllowed("Apple").Should().BeFalse();
        settings.IsSmartSpeakerAllowed("Amazon").Should().BeFalse();
        settings.IsSmartSpeakerAllowed("Google").Should().BeFalse();
    }

    [Theory]
    [InlineData("apple")]
    [InlineData("Apple")]
    [InlineData("APPLE")]
    [InlineData("Apple Inc")]
    [InlineData("Apple, Inc.")]
    public void IsSmartSpeakerAllowed_AllowAppleStreaming_CaseInsensitive(string vendor)
    {
        var settings = new DeviceAllowanceSettings { AllowAppleStreamingOnMainNetwork = true };

        settings.IsSmartSpeakerAllowed(vendor).Should().BeTrue();
    }

    #endregion

    #region Default Settings Tests

    [Fact]
    public void Default_HasNoAllowances()
    {
        var settings = DeviceAllowanceSettings.Default;

        settings.AllowAppleStreamingOnMainNetwork.Should().BeFalse();
        settings.AllowAllStreamingOnMainNetwork.Should().BeFalse();
        settings.AllowNameBrandTVsOnMainNetwork.Should().BeFalse();
        settings.AllowAllTVsOnMainNetwork.Should().BeFalse();
    }

    [Fact]
    public void Default_ReturnsNewInstance()
    {
        var settings1 = DeviceAllowanceSettings.Default;
        var settings2 = DeviceAllowanceSettings.Default;

        settings1.Should().NotBeSameAs(settings2);
    }

    #endregion
}
