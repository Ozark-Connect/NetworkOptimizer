using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The warnings widget is optional and its shape varies across UniFi Network versions, so parsing
/// must degrade to null instead of throwing. These tests pin that.
/// </summary>
public class UniFiFirmwareWarningsTests
{
    [Fact]
    public void TryParse_ReadsTheDocumentedShape()
    {
        var json = """
        {
          "meta": { "rc": "ok" },
          "data": [
            {
              "has_upgradable_devices": true,
              "firmware_last_changed": 1786717611,
              "last_controller_update_query": 0,
              "last_controller_update_query_status": "failed",
              "last_firmware_update_query": 1786718405,
              "last_firmware_update_query_status": "ok",
              "unsupported_device_count": 0,
              "eol_device_count": 2,
              "lts_device_count": 1,
              "lte_subscription_past_due_for": [],
              "controller_low_disk_space": false,
              "request_analytics_approvement": false
            }
          ]
        }
        """;

        var warnings = UniFiFirmwareWarnings.TryParse(json);

        warnings.Should().NotBeNull();
        warnings!.HasUpgradableDevices.Should().BeTrue();
        warnings.UnsupportedDeviceCount.Should().Be(0);
        warnings.EolDeviceCount.Should().Be(2);
        warnings.LtsDeviceCount.Should().Be(1);
        warnings.ControllerLowDiskSpace.Should().BeFalse();
        warnings.LastFirmwareUpdateQueryStatus.Should().Be("ok");
    }

    [Fact]
    public void TryParse_WithUnknownAndMissingFields_KeepsWhatItRecognizes()
    {
        var json = """
        {
          "meta": { "rc": "ok" },
          "data": [
            { "has_upgradable_devices": false, "some_future_field": { "nested": [1, 2, 3] } }
          ]
        }
        """;

        var warnings = UniFiFirmwareWarnings.TryParse(json);

        warnings.Should().NotBeNull();
        warnings!.HasUpgradableDevices.Should().BeFalse();
        warnings.EolDeviceCount.Should().BeNull();
        warnings.ControllerLowDiskSpace.Should().BeNull();
        warnings.LastFirmwareUpdateQueryStatus.Should().BeNull();
    }

    [Fact]
    public void TryParse_WithStringifiedCounts_StillReads()
    {
        var json = """
        {"meta":{"rc":"ok"},"data":[{"eol_device_count":"3","lts_device_count":""}]}
        """;

        var warnings = UniFiFirmwareWarnings.TryParse(json);

        warnings.Should().NotBeNull();
        warnings!.EolDeviceCount.Should().Be(3);
        warnings.LtsDeviceCount.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>Login</body></html>")]
    [InlineData("not json at all")]
    [InlineData("""{"meta":{"rc":"ok"}}""")]
    [InlineData("""{"meta":{"rc":"ok"},"data":[]}""")]
    [InlineData("""{"meta":{"rc":"ok"},"data":"unexpected"}""")]
    [InlineData("""{"meta":{"rc":"ok"},"data":["unexpected"]}""")]
    public void TryParse_WithAnythingUnexpected_ReturnsNull(string? json)
    {
        UniFiFirmwareWarnings.TryParse(json).Should().BeNull();
    }

    [Fact]
    public void TryParse_WithAFieldOfTheWrongKind_ReturnsNull()
    {
        var json = """
        {"meta":{"rc":"ok"},"data":[{"has_upgradable_devices":{"unexpected":"object"}}]}
        """;

        UniFiFirmwareWarnings.TryParse(json).Should().BeNull();
    }
}
