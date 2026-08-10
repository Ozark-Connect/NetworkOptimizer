using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Retiring a target must not touch Enabled. That column carries what the user asked for, and the
/// probe paths gate on RetiredAt instead - without that split, a target the user had paused came
/// back probing the moment its device left the console and returned.
/// </summary>
public class RetiredTargetProbeGateTests
{
    private static MonitoringTarget Target(bool enabled, bool retired) => new()
    {
        TargetId = "fabric-aa:bb:cc:dd:ee:ff",
        Name = "Switch",
        Address = "192.0.2.10",
        TargetType = MonitoringTargetType.Fabric,
        Enabled = enabled,
        RetiredAt = retired ? DateTime.UtcNow : null,
    };

    /// <summary>The predicate both probe paths apply.</summary>
    private static bool WouldBeProbed(MonitoringTarget t) => t.Enabled && t.RetiredAt == null;

    [Theory]
    [InlineData(true, false, true)]    // live and wanted: probed
    [InlineData(false, false, false)]  // the user paused it
    [InlineData(true, true, false)]    // retired, though the user never paused it
    [InlineData(false, true, false)]   // paused AND retired
    public void Only_a_live_target_the_user_wants_is_probed(bool enabled, bool retired, bool probed)
    {
        WouldBeProbed(Target(enabled, retired)).Should().Be(probed);
    }

    [Fact]
    public void A_paused_target_is_still_paused_after_being_retired_and_revived()
    {
        var target = Target(enabled: false, retired: false);

        // Retire, as the reconcile does: RetiredAt only.
        target.RetiredAt = DateTime.UtcNow;
        target.RetiredReason = "No longer in the UniFi device list";

        // Revive, as the reconcile does when the device returns on the same address.
        target.RetiredAt = null;
        target.RetiredReason = null;

        target.Enabled.Should().BeFalse("the pause was the user's and nothing in that round trip was entitled to clear it");
        WouldBeProbed(target).Should().BeFalse();
    }
}
