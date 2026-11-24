using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.DeviceRestart;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.DeviceRestart;

/// <summary>
/// The restart rules that need no I/O: root-first order, the once-each check, the lockout's
/// reach (self, path, subtree), and when a PoE power-cycle is allowed.
/// </summary>
public class RestartRulesTests
{
    private const string Gw = "aa:bb:cc:00:00:01";
    private const string Sw = "aa:bb:cc:00:00:02";
    private const string Ap = "aa:bb:cc:00:00:03";
    private const string Ap2 = "aa:bb:cc:00:00:04";

    private static readonly DateTime Now = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);
    private static readonly RestartSiteState Quiet = new(null, false);

    private static RestartTopology Tree(int apState = 1, long apUptime = 86400, double apWatts = 6.2, int swState = 1) => new(
    [
        new RestartNode(Gw, "Gateway", DeviceType.Gateway, null, null, 1, 86400),
        new RestartNode(Sw, "Office Switch", DeviceType.Switch, Gw, 1, swState, 86400,
            new Dictionary<int, double> { [7] = apWatts }),
        new RestartNode(Ap, "Living Room", DeviceType.AccessPoint, Sw, 7, apState, apUptime),
        new RestartNode(Ap2, "Back Yard", DeviceType.AccessPoint, Sw, 8, 1, 86400),
    ]);

    private static DeviceRestartRequest Request(int id, string mac, DateTime due, DeviceRestartStatus status = DeviceRestartStatus.Pending) => new()
    {
        Id = id, DeviceMac = mac, DueAtUtc = due, CreatedAt = due.AddHours(-1), Status = status,
    };

    [Fact]
    public void Topology_DepthAndSubtree_FollowTheUplinks()
    {
        var tree = Tree();

        tree.Depth(Gw).Should().Be(0);
        tree.Depth(Ap).Should().Be(2);
        tree.Subtree(Sw).Select(n => n.Mac).Should().BeEquivalentTo([Ap, Ap2]);
        tree.IsAbove(Gw, Ap).Should().BeTrue();
        tree.IsAbove(Ap, Gw).Should().BeFalse();
    }

    [Fact]
    public void Topology_AGatewayReportingASwitchAsItsUplink_StaysTheRoot()
    {
        var tree = new RestartTopology(
        [
            new RestartNode(Gw, "Gateway", DeviceType.Gateway, Sw, null, 1, 86400),
            new RestartNode(Sw, "Office Switch", DeviceType.Switch, Gw, 4, 1, 86400),
            new RestartNode(Ap, "Main Kitchen", DeviceType.AccessPoint, Gw, 3, 1, 86400),
        ]);

        tree.Subtree(Sw).Should().BeEmpty();
        tree.Subtree(Gw).Select(n => n.Mac).Should().BeEquivalentTo([Sw, Ap]);
        tree.Children(Gw).Select(n => n.Mac).Should().BeEquivalentTo([Sw, Ap]);
    }

    [Fact]
    public void Topology_AnUplinkLoop_PutsNeitherDeviceBelowTheOther()
    {
        var tree = new RestartTopology(
        [
            new RestartNode(Sw, "Office Switch", DeviceType.Switch, Ap, 1, 1, 86400),
            new RestartNode(Ap, "Main Kitchen", DeviceType.AccessPoint, Sw, 1, 1, 86400),
        ]);

        tree.Subtree(Sw).Should().BeEmpty();
        tree.Subtree(Ap).Should().BeEmpty();
    }

    [Fact]
    public void Topology_ParentLoop_DoesNotSpin()
    {
        var tree = new RestartTopology(
        [
            new RestartNode(Sw, "A", DeviceType.Switch, Ap, null, 1, 1),
            new RestartNode(Ap, "B", DeviceType.AccessPoint, Sw, null, 1, 1),
        ]);

        tree.PathToRoot(Ap).Should().HaveCount(1);
    }

    [Fact]
    public void Next_DueTogether_RootFirst()
    {
        var open = new[] { Request(1, Ap, Now.AddMinutes(-1)), Request(2, Gw, Now), Request(3, Sw, Now.AddMinutes(-5)) };

        RestartRules.Next(open, Tree(), Now)!.DeviceMac.Should().Be(Gw);
    }

    [Fact]
    public void Next_NotYetDue_IsNotPicked()
    {
        var open = new[] { Request(1, Gw, Now.AddMinutes(10)), Request(2, Ap, Now) };

        RestartRules.Next(open, Tree(), Now)!.DeviceMac.Should().Be(Ap);
    }

    [Fact]
    public void Next_CommandedRequest_KeepsTheTurn()
    {
        var inFlight = Request(1, Ap, Now.AddMinutes(-3), DeviceRestartStatus.Running);
        inFlight.LastCommandAt = Now.AddMinutes(-1);
        var open = new[] { inFlight, Request(2, Gw, Now) };

        RestartRules.Next(open, Tree(), Now).Should().BeSameAs(inFlight);
    }

    [Fact]
    public void Candidates_ParentUnderWay_ChildWaits_SiblingSubtreeDoesNot()
    {
        // The switch is between attempts (Running, not commanded); the AP below it must not jump it.
        var tree = new RestartTopology(Tree().All.Append(new RestartNode("aa:bb:cc:00:00:09", "Garage Switch", DeviceType.Switch, Gw, 2, 1, 86400)));
        var parent = Request(1, Sw, Now.AddMinutes(5), DeviceRestartStatus.Running);
        var child = Request(2, Ap, Now);
        var unrelated = Request(3, "aa:bb:cc:00:00:09", Now);

        RestartRules.Candidates([parent, child, unrelated], tree, Now).Should().BeEquivalentTo([unrelated]);
    }

    [Fact]
    public void Candidates_ParentHeldByLockout_ChildWaitsToo()
    {
        var parent = Request(1, Sw, Now, DeviceRestartStatus.Waiting);
        var child = Request(2, Ap, Now);

        RestartRules.Candidates([parent, child], Tree(), Now).Should().BeEquivalentTo([parent]);
    }

    [Fact]
    public void AlreadyRestarted_BootedAfterRequestNearItsDueTime_Counts()
    {
        // Booted 4 minutes ago (its switch cut PoE), due now.
        var tree = Tree(apUptime: 240);

        RestartRules.AlreadyRestarted(Request(1, Ap, Now), tree.Find(Ap)!, Now).Should().BeTrue();
    }

    [Fact]
    public void AlreadyRestarted_BootedLongBeforeDue_DoesNotCount()
    {
        var tree = Tree(apUptime: 3600);

        RestartRules.AlreadyRestarted(Request(1, Ap, Now), tree.Find(Ap)!, Now).Should().BeFalse();
    }

    [Fact]
    public void AlreadyRestarted_BootedBeforeTheRequestWasMade_DoesNotCount()
    {
        var tree = Tree(apUptime: 240);
        var request = Request(1, Ap, Now);
        request.CreatedAt = Now.AddMinutes(-1);

        RestartRules.AlreadyRestarted(request, tree.Find(Ap)!, Now).Should().BeFalse();
    }

    [Fact]
    public void Lockout_ApBelowSwitchUpgrading_LocksTheSwitch()
    {
        var tree = Tree(apState: RestartNode.UpgradingState);

        RestartRules.Lockout(tree, Sw, Quiet, Now).Should().Be("Living Room is upgrading its firmware.");
    }

    [Fact]
    public void Lockout_SwitchAboveProvisioning_LocksTheAp()
    {
        var tree = Tree(swState: RestartNode.ProvisioningState);

        RestartRules.Lockout(tree, Ap, Quiet, Now).Should().Be("Office Switch is provisioning.");
    }

    [Fact]
    public void Lockout_SiblingUpgrading_DoesNotLock()
    {
        var tree = new RestartTopology(Tree().All.Select(n => n.Mac == Ap2 ? n with { State = RestartNode.UpgradingState } : n));

        RestartRules.Lockout(tree, Ap, Quiet, Now).Should().BeNull();
    }

    [Fact]
    public void Lockout_RolloutRunning_LocksTheSite()
    {
        var site = new RestartSiteState(new SiteRolloutStatus(1, SiteRolloutStage.Running, Now.AddHours(-1)), false);

        RestartRules.Lockout(Tree(), Ap, site, Now).Should().Be("A Firmware Rollout is in progress on this site.");
    }

    [Theory]
    [InlineData(SiteRolloutStage.Paused)]
    [InlineData(SiteRolloutStage.Soaking)]
    public void Lockout_RolloutNotFlashing_DoesNotLock(SiteRolloutStage stage)
    {
        // A paused or soaking rollout can sit for days; a device actually flashing is caught by its state.
        var site = new RestartSiteState(new SiteRolloutStatus(1, stage, Now.AddHours(-1)), false);

        RestartRules.Lockout(Tree(), Ap, site, Now).Should().BeNull();
    }

    [Fact]
    public void Lockout_RolloutScheduledWithNoTime_DoesNotLock()
    {
        var site = new RestartSiteState(new SiteRolloutStatus(1, SiteRolloutStage.Scheduled, null), false);

        RestartRules.Lockout(Tree(), Ap, site, Now).Should().BeNull();
    }

    [Fact]
    public void Lockout_RolloutScheduledSoon_Locks_LaterDoesNot()
    {
        var soon = new RestartSiteState(new SiteRolloutStatus(1, SiteRolloutStage.Scheduled, Now.AddMinutes(40)), false);
        var later = new RestartSiteState(new SiteRolloutStatus(1, SiteRolloutStage.Scheduled, Now.AddHours(3)), false);

        RestartRules.Lockout(Tree(), Ap, soon, Now).Should().Be("A Firmware Rollout starts in 40 minutes.");
        RestartRules.Lockout(Tree(), Ap, later, Now).Should().BeNull();
    }

    [Fact]
    public void Lockout_FinishedRollout_DoesNotLock()
    {
        var site = new RestartSiteState(new SiteRolloutStatus(1, SiteRolloutStage.Completed, Now.AddHours(-2)), false);

        RestartRules.Lockout(Tree(), Ap, site, Now).Should().BeNull();
    }

    [Fact]
    public void Lockout_ChangeControlApplying_LocksTheSite()
    {
        RestartRules.Lockout(Tree(), Ap, new RestartSiteState(null, true), Now).Should().NotBeNull();
    }

    [Fact]
    public void PoweringPort_PoeDrawOnUplinkPort_IsTheSwitchPort()
    {
        RestartRules.PoweringPort(Tree(), Ap, null).Should().Be(new PoePort(Sw, "Office Switch", 7));
    }

    [Fact]
    public void PoweringPort_ZeroNow_ButDrewPowerWhileUp_StillAllowed()
    {
        // A hung AP's port can read 0 W; the reading from while it was up decides.
        RestartRules.PoweringPort(Tree(apState: 0, apWatts: 0), Ap, 5.5).Should().NotBeNull();
    }

    [Fact]
    public void PoweringPort_NeverDrewPower_IsNull()
    {
        RestartRules.PoweringPort(Tree(apWatts: 0), Ap, null).Should().BeNull();
    }

    [Fact]
    public void PoweringPort_SwitchOffline_IsNull()
    {
        RestartRules.PoweringPort(Tree(swState: 0), Ap, 6).Should().BeNull();
    }

    [Fact]
    public void PoweringPort_GatewayPortWithNoPoe_IsNull()
    {
        // The switch hangs off gateway port 1, which delivers no power and never did.
        RestartRules.PoweringPort(Tree(), Sw, null).Should().BeNull();
    }

    [Fact]
    public void PoweringPort_GatewayPoePort_IsAllowed()
    {
        var tree = new RestartTopology(
        [
            new RestartNode(Gw, "Gateway", DeviceType.Gateway, null, null, 1, 86400, new Dictionary<int, double> { [3] = 7.1 }),
            new RestartNode(Ap, "Living Room", DeviceType.AccessPoint, Gw, 3, 1, 86400),
        ]);

        RestartRules.PoweringPort(tree, Ap, null).Should().Be(new PoePort(Gw, "Gateway", 3));
    }
}
