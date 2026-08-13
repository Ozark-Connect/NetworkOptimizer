using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Retirement clears Enabled, because a dozen readers treat that column as "is this row live".
/// Enabled is also where a user's pause lives, so it is remembered and handed back on revival -
/// without that, a target the user had paused came back probing once its device left the console
/// and returned.
/// </summary>
public class RetiredTargetProbeGateTests
{
    private static MonitoringTarget Target(bool enabled) => new()
    {
        TargetId = "fabric-aa:bb:cc:dd:ee:ff",
        Name = "Switch",
        Address = "192.0.2.10",
        TargetType = MonitoringTargetType.Fabric,
        Enabled = enabled,
    };

    /// <summary>The predicate both probe paths apply.</summary>
    private static bool WouldBeProbed(MonitoringTarget t) => t.Enabled && t.RetiredAt == null;

    [Fact]
    public void Retiring_stops_the_probing_and_reads_as_inactive_to_an_Enabled_only_reader()
    {
        var target = Target(enabled: true);

        MonitoringCollectionAgent.ApplyRetirement(target, "No longer in the UniFi device list");

        target.Enabled.Should().BeFalse("readers that filter on Enabled alone must not count it as active");
        target.RetiredAt.Should().NotBeNull();
        WouldBeProbed(target).Should().BeFalse();
    }

    [Fact]
    public void A_target_the_user_had_running_comes_back_running()
    {
        var target = Target(enabled: true);

        MonitoringCollectionAgent.ApplyRetirement(target, "Address changed to 192.0.2.20");
        MonitoringCollectionAgent.ApplyRevival(target, fallbackEnabled: true);

        target.Enabled.Should().BeTrue();
        target.RetiredAt.Should().BeNull();
        target.RetiredReason.Should().BeNull();
        target.EnabledBeforeRetire.Should().BeNull("the remembered value has been handed back and is spent");
        WouldBeProbed(target).Should().BeTrue();
    }

    [Fact]
    public void A_target_the_user_had_paused_comes_back_paused()
    {
        var target = Target(enabled: false);

        MonitoringCollectionAgent.ApplyRetirement(target, "No longer in the UniFi device list");
        // fallbackEnabled is what a brand-new target would get; the remembered pause must beat it.
        MonitoringCollectionAgent.ApplyRevival(target, fallbackEnabled: true);

        target.Enabled.Should().BeFalse("the pause was the user's and nothing in that round trip was entitled to clear it");
        WouldBeProbed(target).Should().BeFalse();
    }

    [Fact]
    public void A_replacement_inherits_the_state_its_predecessor_was_in_before_being_retired()
    {
        // The order the reconcile actually runs in: the predecessor is retired, and only then is
        // the replacement's state decided from it. Reading Enabled at that point gives false for
        // every moved device - which shipped once, and is what this pins down.
        var predecessor = Target(enabled: true);
        MonitoringCollectionAgent.ApplyRetirement(predecessor, "Address changed to 192.0.2.20");

        MonitoringCollectionAgent.ReplacementEnabled(predecessor, isFlex25G: false)
            .Should().BeTrue("the user had it running, so its replacement runs");
    }

    [Fact]
    public void A_replacement_of_a_paused_target_starts_paused()
    {
        var predecessor = Target(enabled: false);
        MonitoringCollectionAgent.ApplyRetirement(predecessor, "Address changed to 192.0.2.20");

        MonitoringCollectionAgent.ReplacementEnabled(predecessor, isFlex25G: false).Should().BeFalse();
    }

    [Fact]
    public void The_inherited_state_is_the_same_whether_it_is_read_before_or_after_retirement()
    {
        var before = Target(enabled: true);
        var after = Target(enabled: true);
        MonitoringCollectionAgent.ApplyRetirement(after, "Address changed to 192.0.2.20");

        MonitoringCollectionAgent.ReplacementEnabled(after, isFlex25G: false)
            .Should().Be(MonitoringCollectionAgent.ReplacementEnabled(before, isFlex25G: false),
                "reordering the retire and the decision must not change the answer");
    }

    [Fact]
    public void A_brand_new_device_starts_probing_and_a_Flex_25G_never_does()
    {
        MonitoringCollectionAgent.ReplacementEnabled(null, isFlex25G: false).Should().BeTrue();
        MonitoringCollectionAgent.ReplacementEnabled(null, isFlex25G: true).Should().BeFalse();

        var running = Target(enabled: true);
        MonitoringCollectionAgent.ReplacementEnabled(running, isFlex25G: true)
            .Should().BeFalse("the Flex rule is ours and outranks what it inherits");
    }

    [Fact]
    public void A_row_retired_before_the_remembered_value_existed_falls_back()
    {
        // Rows retired by an earlier build carry RetiredAt with no EnabledBeforeRetire.
        var target = Target(enabled: false);
        target.RetiredAt = DateTime.UtcNow;
        target.EnabledBeforeRetire = null;

        MonitoringCollectionAgent.ApplyRevival(target, fallbackEnabled: true);

        target.Enabled.Should().BeTrue("with nothing remembered, it starts as a fresh target would");
    }
}
