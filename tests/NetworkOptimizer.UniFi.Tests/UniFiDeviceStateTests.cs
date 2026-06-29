using FluentAssertions;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class UniFiDeviceStateTests
{
    [Theory]
    // Online bucket - green, actionable.
    [InlineData(1, DeviceStatusKind.Online, "Online")]
    [InlineData(3, DeviceStatusKind.Online, "Update Available")]
    // Transitional bucket - yellow, not a fault but not actionable yet.
    [InlineData(2, DeviceStatusKind.Transitional, "Pending")]
    [InlineData(4, DeviceStatusKind.Transitional, "Updating")]
    [InlineData(5, DeviceStatusKind.Transitional, "Provisioning")]
    [InlineData(7, DeviceStatusKind.Transitional, "Adopting")]
    [InlineData(8, DeviceStatusKind.Transitional, "Deleting")]
    // Offline bucket - grey. All three map to the same label.
    [InlineData(0, DeviceStatusKind.Offline, "Offline")]
    [InlineData(6, DeviceStatusKind.Offline, "Offline")]
    [InlineData(9, DeviceStatusKind.Offline, "Offline")]
    // Error bucket - red.
    [InlineData(10, DeviceStatusKind.Error, "Adoption Failed")]
    [InlineData(11, DeviceStatusKind.Error, "Isolated")]
    [InlineData(13, DeviceStatusKind.Error, "Incorrect Topology")]
    // Unknown / unused values fall back to Offline rather than throwing.
    [InlineData(12, DeviceStatusKind.Offline, "Offline")]
    [InlineData(99, DeviceStatusKind.Offline, "Offline")]
    public void ToStatus_maps_state_to_kind_and_label(int state, DeviceStatusKind kind, string label)
    {
        var status = UniFiDeviceStateMap.ToStatus(state);
        status.Kind.Should().Be(kind);
        status.Label.Should().Be(label);
    }

    [Theory]
    // Only the Online bucket (connected, update-available) is actionable; provisioning is NOT.
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(5, false)]
    [InlineData(4, false)]
    [InlineData(0, false)]
    [InlineData(10, false)]
    public void IsOnline_is_true_only_for_the_online_bucket(int state, bool expected)
        => UniFiDeviceStateMap.IsOnline(state).Should().Be(expected);

    [Fact]
    public void Provisioning_uses_the_yellow_dot_and_is_not_offline()
    {
        var status = UniFiDeviceStateMap.ToStatus((int)UniFiDeviceState.Provisioning);
        status.CssClass.Should().Be("provisioning");
        status.IsOffline.Should().BeFalse();
        status.IsOnline.Should().BeFalse();
    }
}
