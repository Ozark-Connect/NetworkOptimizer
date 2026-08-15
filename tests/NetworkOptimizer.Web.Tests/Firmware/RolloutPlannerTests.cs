using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The planner decides what goes down when, and it is the only thing standing between an
/// overnight rollout and a site that takes itself off the air. These cover the ordering
/// contract (outer-to-inner, gateway last), the safety rules layered on top of it (per-SKU
/// canary, mesh child before parent, coverage-aware AP parallelism), and the timeline the
/// wizard quotes back to the user.
/// </summary>
public class RolloutPlannerTests
{
    private const string GatewayMac = "aa:bb:cc:dd:ee:01";
    private const string DistSwitchMac = "aa:bb:cc:dd:ee:02";
    private const string AccessSwitchMac = "aa:bb:cc:dd:ee:03";
    private const string ApMac = "aa:bb:cc:dd:ee:04";

    private static PlannerDevice Device(
        string mac,
        DeviceType type,
        string model,
        string name,
        string? uplinkMac = null,
        bool upgradable = true,
        bool wirelessUplink = false,
        string? meshInterface = null,
        string? displayModel = null,
        string? ipAddress = null) => new()
        {
            Mac = mac,
            Name = name,
            Model = model,
            DisplayModel = displayModel ?? model,
            Type = type,
            Upgradable = upgradable,
            FromVersion = "1.0.0",
            ToVersion = "1.1.0",
            UplinkMac = uplinkMac,
            WirelessUplink = wirelessUplink,
            MeshUplinkInterface = meshInterface,
            IpAddress = ipAddress,
        };

    private static PlannerDevice Ap(string mac, string name, string model = "SKU-AP1", string? uplinkMac = null,
        bool upgradable = true, bool wirelessUplink = false, string? meshInterface = null,
        string? displayModel = null, string? ipAddress = null) =>
        Device(mac, DeviceType.AccessPoint, model, name, uplinkMac, upgradable, wirelessUplink, meshInterface,
            displayModel, ipAddress);

    private static PlannerDevice Sw(string mac, string name, string model = "SKU-SW1", string? uplinkMac = null,
        bool upgradable = true) =>
        Device(mac, DeviceType.Switch, model, name, uplinkMac, upgradable);

    private static PlannerDevice Gw(string mac = GatewayMac, string name = "Gateway-1", string model = "SKU-GW1",
        bool upgradable = true, string? displayModel = null) =>
        Device(mac, DeviceType.Gateway, model, name, uplinkMac: null, upgradable, displayModel: displayModel);

    private static FirmwareRolloutSettings Settings(
        string globalChannel = FirmwareChannels.Release,
        string perDeviceTypeChannelsJson = "{}",
        string perSkuChannelsJson = "{}",
        string exclusionsJson = "{}",
        FirmwareSpacingProfile profile = FirmwareSpacingProfile.Balanced,
        string? advancedSpacingJson = null,
        bool includeUniFiNetwork = false,
        bool includeUniFiOs = false) => new()
        {
            GlobalChannel = globalChannel,
            PerDeviceTypeChannelsJson = perDeviceTypeChannelsJson,
            PerSkuChannelsJson = perSkuChannelsJson,
            ExclusionsJson = exclusionsJson,
            SpacingProfile = profile,
            AdvancedSpacingJson = advancedSpacingJson,
            IncludeUniFiNetwork = includeUniFiNetwork,
            IncludeUniFiOs = includeUniFiOs,
        };

    private static RolloutPlanResult Plan(
        IEnumerable<PlannerDevice> devices,
        FirmwareRolloutSettings? settings = null,
        IApNeighborOracle? neighbors = null,
        string currentConsoleChannel = FirmwareChannels.Release,
        FirmwareTimingEstimator? estimator = null) =>
        new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = devices.ToList(),
            Settings = settings ?? Settings(),
            Estimator = estimator ?? new FirmwareTimingEstimator(),
            CurrentConsoleChannel = currentConsoleChannel,
            Neighbors = neighbors,
        });

    private static List<string> WaveMacs(PlanWave wave) => wave.Steps.Select(s => s.Mac).ToList();

    private static int WaveOf(RolloutPlanDocument doc, string mac) =>
        doc.Waves.Single(w => w.Steps.Any(s => s.Mac == mac)).Number;

    private static PlanWaveStep StepOf(RolloutPlanDocument doc, string mac) =>
        doc.Waves.SelectMany(w => w.Steps).Single(s => s.Mac == mac);

    // ---- Ordering -------------------------------------------------------------------

    [Fact]
    public void Plan_OrdersOuterToInner_LeavesThenDistributionThenGateway()
    {
        var devices = new[]
        {
            Gw(),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Sw(AccessSwitchMac, "Switch-2", "SKU-SW2", DistSwitchMac),
            Ap(ApMac, "AP-1", "SKU-AP1", AccessSwitchMac),
        };

        var doc = Plan(devices).Document;

        doc.Waves.Select(w => WaveMacs(w).Single())
            .Should().Equal(ApMac, AccessSwitchMac, DistSwitchMac, GatewayMac);
    }

    [Fact]
    public void Plan_GatewayIsAlwaysTheVeryLastWave()
    {
        var devices = new[]
        {
            Gw(),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", DistSwitchMac),
        };

        var doc = Plan(devices).Document;

        doc.Waves.Last().Steps.Should().ContainSingle().Which.Mac.Should().Be(GatewayMac);
    }

    [Fact]
    public void Plan_WithinOneLevel_AccessPointsRunBeforeSwitches()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
        };

        var doc = Plan(devices).Document;

        WaveOf(doc, ApMac).Should().BeLessThan(WaveOf(doc, DistSwitchMac));
    }

    // ---- Depth ----------------------------------------------------------------------

    [Fact]
    public void ComputeDepths_Chain_CountsHopsFromTheGateway()
    {
        var devices = new[]
        {
            Gw(),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Sw(AccessSwitchMac, "Switch-2", "SKU-SW2", DistSwitchMac),
            Ap(ApMac, "AP-1", "SKU-AP1", AccessSwitchMac),
        };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[GatewayMac].Should().Be(0);
        depths[DistSwitchMac].Should().Be(1);
        depths[AccessSwitchMac].Should().Be(2);
        depths[ApMac].Should().Be(3);
    }

    [Fact]
    public void ComputeDepths_ChainDiscoveredParentFirst_StillIncreasesDownstream()
    {
        // Reverse input order exercises the "parent depth already known" branch.
        var devices = new[]
        {
            Ap(ApMac, "AP-1", "SKU-AP1", AccessSwitchMac),
            Sw(AccessSwitchMac, "Switch-2", "SKU-SW2", DistSwitchMac),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Gw(),
        };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[GatewayMac].Should().Be(0);
        depths[DistSwitchMac].Should().Be(1);
        depths[AccessSwitchMac].Should().Be(2);
        depths[ApMac].Should().Be(3);
    }

    [Fact]
    public void ComputeDepths_SelfReferencingUplink_IsADeepLeaf()
    {
        var devices = new[] { Gw(), Ap(ApMac, "AP-1", "SKU-AP1", uplinkMac: ApMac) };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[ApMac].Should().BeGreaterThanOrEqualTo(1000);
        depths[GatewayMac].Should().Be(0);
    }

    [Fact]
    public void ComputeDepths_TwoDeviceCycle_IsGuardedAndBothAreDeepLeaves()
    {
        var a = "aa:bb:cc:dd:ee:0a";
        var b = "aa:bb:cc:dd:ee:0b";
        var devices = new[] { Ap(a, "AP-A", "SKU-AP1", uplinkMac: b), Ap(b, "AP-B", "SKU-AP2", uplinkMac: a) };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[a].Should().BeGreaterThanOrEqualTo(1000);
        depths[b].Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public void ComputeDepths_UnknownParent_IsADeepLeaf()
    {
        var devices = new[] { Gw(), Ap(ApMac, "AP-1", "SKU-AP1", uplinkMac: "aa:bb:cc:dd:ee:99") };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[ApMac].Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public void ComputeDepths_GatewayWithAnUpstreamOutsideTheFleet_StaysAtTheRoot()
    {
        var devices = new[] { Device(GatewayMac, DeviceType.Gateway, "SKU-GW1", "Gateway-1", "aa:bb:cc:dd:ee:99") };

        RolloutPlanner.ComputeDepths(devices)[GatewayMac].Should().Be(0);
    }

    [Fact]
    public void ComputeDepths_MeshChild_IsDeeperThanItsParent()
    {
        var meshChild = "aa:bb:cc:dd:ee:05";
        var devices = new[]
        {
            Gw(),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
            Ap(meshChild, "AP-2", "SKU-AP2", ApMac, wirelessUplink: true, meshInterface: "vwiresta0"),
        };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[meshChild].Should().Be(depths[ApMac] + 1);
    }

    [Fact]
    public void ComputeDepths_OrphanSubtree_KeepsChildrenDeeperThanParents()
    {
        var devices = new[]
        {
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", uplinkMac: "aa:bb:cc:dd:ee:99"),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
        };

        var depths = RolloutPlanner.ComputeDepths(devices);

        depths[ApMac].Should().Be(depths[DistSwitchMac] + 1);
    }

    // ---- Channel grouping -----------------------------------------------------------

    [Fact]
    public void ResolveChannel_NoOverrides_UsesTheGlobalChannel()
    {
        var settings = Settings(globalChannel: FirmwareChannels.ReleaseCandidate);

        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-1"), settings)
            .Should().Be(FirmwareChannels.ReleaseCandidate);
    }

    [Fact]
    public void ResolveChannel_EmptyGlobalChannel_FallsBackToRelease()
    {
        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-1"), Settings(globalChannel: ""))
            .Should().Be(FirmwareChannels.Release);
    }

    [Fact]
    public void ResolveChannel_PerSkuOverride_BeatsPerTypeAndGlobal()
    {
        var settings = Settings(
            globalChannel: FirmwareChannels.Release,
            perDeviceTypeChannelsJson: """{"uap":"release-candidate"}""",
            perSkuChannelsJson: """{"SKU-AP1":"beta"}""");

        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-1", "SKU-AP1"), settings).Should().Be(FirmwareChannels.Beta);
        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-2", "SKU-AP2"), settings)
            .Should().Be(FirmwareChannels.ReleaseCandidate);
    }

    [Fact]
    public void ResolveChannel_PerTypeOverride_AcceptsTheUniFiTypeCodeKey()
    {
        var settings = Settings(perDeviceTypeChannelsJson: """{"uap":"beta","usw":"release-candidate"}""");

        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-1"), settings).Should().Be(FirmwareChannels.Beta);
        RolloutPlanner.ResolveChannel(Sw(DistSwitchMac, "Switch-1"), settings)
            .Should().Be(FirmwareChannels.ReleaseCandidate);
    }

    [Fact]
    public void ResolveChannel_PerTypeOverride_AcceptsTheEnumNameKey()
    {
        var settings = Settings(perDeviceTypeChannelsJson: """{"AccessPoint":"beta","Gateway":"release-candidate"}""");

        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-1"), settings).Should().Be(FirmwareChannels.Beta);
        RolloutPlanner.ResolveChannel(Gw(), settings).Should().Be(FirmwareChannels.ReleaseCandidate);
    }

    [Fact]
    public void ResolveChannel_MalformedOverrideJson_FallsBackToTheGlobalChannel()
    {
        var settings = Settings(
            globalChannel: FirmwareChannels.Release,
            perDeviceTypeChannelsJson: "{not json",
            perSkuChannelsJson: "[1,2]");

        RolloutPlanner.ResolveChannel(Ap(ApMac, "AP-1"), settings).Should().Be(FirmwareChannels.Release);
    }

    [Fact]
    public void Plan_GroupMatchingTheConsoleChannel_RunsFirstEvenWhenSmaller()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Sw(AccessSwitchMac, "Switch-2", "SKU-SW2", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
        };
        var settings = Settings(perDeviceTypeChannelsJson: """{"usw":"beta"}""");

        var doc = Plan(devices, settings, currentConsoleChannel: FirmwareChannels.Release).Document;

        doc.ChannelGroups.Select(g => g.Channel).Should().Equal(FirmwareChannels.Release, FirmwareChannels.Beta);
        doc.ChannelGroups[0].RequiresConsoleChange.Should().BeFalse();
        doc.ChannelGroups[1].RequiresConsoleChange.Should().BeTrue();
        WaveOf(doc, ApMac).Should().BeLessThan(WaveOf(doc, DistSwitchMac));
    }

    [Fact]
    public void Plan_GatewayGroup_RunsLastEvenWhenItMatchesTheConsoleAndIsSmaller()
    {
        var devices = new[]
        {
            Gw(),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", GatewayMac),
            Ap("aa:bb:cc:dd:ee:06", "AP-3", "SKU-AP3", GatewayMac),
        };
        var settings = Settings(perSkuChannelsJson: """{"SKU-GW1":"beta"}""");

        var doc = Plan(devices, settings, currentConsoleChannel: FirmwareChannels.Beta).Document;

        doc.ChannelGroups.Last().Channel.Should().Be(FirmwareChannels.Beta);
        doc.ChannelGroups.Last().DeviceCount.Should().Be(1);
        doc.Waves.Last().Steps.Should().ContainSingle().Which.Mac.Should().Be(GatewayMac);
    }

    [Fact]
    public void Plan_ChannelGroups_CarryWaveRangesAndDeviceCounts()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", DistSwitchMac),
        };
        var settings = Settings(
            profile: FirmwareSpacingProfile.Conservative,
            perDeviceTypeChannelsJson: """{"usw":"beta"}""");

        var doc = Plan(devices, settings, currentConsoleChannel: FirmwareChannels.Release).Document;

        var release = doc.ChannelGroups.Single(g => g.Channel == FirmwareChannels.Release);
        var beta = doc.ChannelGroups.Single(g => g.Channel == FirmwareChannels.Beta);

        release.DeviceCount.Should().Be(2);
        release.FirstWave.Should().Be(1);
        release.LastWave.Should().Be(2);
        beta.DeviceCount.Should().Be(1);
        beta.FirstWave.Should().Be(3);
        beta.LastWave.Should().Be(3);
        doc.Waves.Where(w => w.Channel == FirmwareChannels.Release).Should().HaveCount(2);
    }

    // ---- Canary ---------------------------------------------------------------------

    [Fact]
    public void Plan_FirstDeviceOfAMultiDeviceSku_GetsASoloCanaryWave()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:06", "AP-3", "SKU-AP1", GatewayMac),
        };

        var result = Plan(devices);

        result.Document.Waves.Should().HaveCount(2);
        var canaryWave = result.Document.Waves[0];
        canaryWave.Steps.Should().ContainSingle();
        canaryWave.Steps[0].Mac.Should().Be(ApMac);
        canaryWave.Steps[0].IsCanary.Should().BeTrue();
        canaryWave.Steps[0].HeldForCanary.Should().BeFalse();

        result.Document.Waves[1].Steps.Should().HaveCount(2);
        result.Document.Waves[1].Steps.Should().OnlyContain(s => s.HeldForCanary && !s.IsCanary);

        result.Steps.Single(s => s.DeviceMac == ApMac).State.Should().Be(FirmwareRolloutStepState.Pending);
        result.Steps.Where(s => s.DeviceMac != ApMac)
            .Should().OnlyContain(s => s.State == FirmwareRolloutStepState.Held);
    }

    [Fact]
    public void Plan_SingleDeviceSku_GetsNeitherCanaryNorHold()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", GatewayMac),
        };

        var result = Plan(devices);

        result.Document.Waves.SelectMany(w => w.Steps)
            .Should().OnlyContain(s => !s.IsCanary && !s.HeldForCanary);
        result.Steps.Should().OnlyContain(s => s.State == FirmwareRolloutStepState.Pending);
    }

    [Fact]
    public void Plan_CanaryBookkeeping_SpansDepthLevels()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Sw(AccessSwitchMac, "Switch-2", "SKU-SW1", DistSwitchMac),
        };

        var doc = Plan(devices).Document;

        // The deeper switch is upgraded first, so it is the SKU's canary and the shallower
        // one is held even though they never share a level.
        StepOf(doc, AccessSwitchMac).IsCanary.Should().BeTrue();
        StepOf(doc, DistSwitchMac).HeldForCanary.Should().BeTrue();
        WaveOf(doc, AccessSwitchMac).Should().BeLessThan(WaveOf(doc, DistSwitchMac));
    }

    [Fact]
    public void Plan_CanaryIsPerSku_OtherSkusKeepRolling()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:06", "AP-3", "SKU-AP2", GatewayMac),
            Ap("aa:bb:cc:dd:ee:07", "AP-4", "SKU-AP2", GatewayMac),
        };

        var doc = Plan(devices).Document;

        doc.Waves.SelectMany(w => w.Steps).Count(s => s.IsCanary).Should().Be(2);
        doc.Waves.SelectMany(w => w.Steps).Count(s => s.HeldForCanary).Should().Be(2);
        StepOf(doc, ApMac).IsCanary.Should().BeTrue();
        StepOf(doc, "aa:bb:cc:dd:ee:06").IsCanary.Should().BeTrue();
    }

    // ---- AP parallelism -------------------------------------------------------------

    [Fact]
    public void Plan_InterferingAps_NeverShareAWave()
    {
        var ap1 = ApMac;
        var ap2 = "aa:bb:cc:dd:ee:05";
        var ap3 = "aa:bb:cc:dd:ee:06";
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors(ap1, ap2);
        oracle.AddNeighbors(ap2, ap3);

        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ap1, "AP-1", "SKU-AP1", GatewayMac),
            Ap(ap2, "AP-2", "SKU-AP2", GatewayMac),
            Ap(ap3, "AP-3", "SKU-AP3", GatewayMac),
        };

        var doc = Plan(devices, neighbors: oracle).Document;

        WaveOf(doc, ap1).Should().NotBe(WaveOf(doc, ap2));
        WaveOf(doc, ap2).Should().NotBe(WaveOf(doc, ap3));
        WaveOf(doc, ap1).Should().Be(WaveOf(doc, ap3));
    }

    [Fact]
    public void Plan_AllApsInterfering_EachGetsItsOwnWave()
    {
        var macs = new[] { ApMac, "aa:bb:cc:dd:ee:05", "aa:bb:cc:dd:ee:06" };
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors(macs[0], macs[1]);
        oracle.AddNeighbors(macs[1], macs[2]);
        oracle.AddNeighbors(macs[0], macs[2]);

        var devices = new List<PlannerDevice> { Gw(upgradable: false) };
        for (var i = 0; i < macs.Length; i++)
        {
            devices.Add(Ap(macs[i], $"AP-{i + 1}", $"SKU-AP{i + 1}", GatewayMac));
        }

        var doc = Plan(devices, neighbors: oracle).Document;

        doc.Waves.Should().HaveCount(3);
        doc.Waves.Should().OnlyContain(w => w.Steps.Count == 1);
    }

    [Fact]
    public void Plan_NullOracle_PacksApsUpToTheCap()
    {
        var devices = new List<PlannerDevice> { Gw(upgradable: false) };
        for (var i = 1; i <= 7; i++)
        {
            devices.Add(Ap($"aa:bb:cc:dd:ee:1{i}", $"AP-{i}", $"SKU-AP{i}", GatewayMac));
        }

        var doc = Plan(devices).Document;

        doc.Waves.Select(w => w.Steps.Count).Should().Equal(3, 3, 1);
    }

    [Fact]
    public void Plan_NonInterferingAps_PackUpToTheCapEvenWithAnOracle()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        var devices = new List<PlannerDevice> { Gw(upgradable: false) };
        for (var i = 1; i <= 4; i++)
        {
            devices.Add(Ap($"aa:bb:cc:dd:ee:1{i}", $"AP-{i}", $"SKU-AP{i}", GatewayMac));
        }

        var doc = Plan(devices, neighbors: oracle).Document;

        doc.Waves.Select(w => w.Steps.Count).Should().Equal(3, 1);
    }

    [Fact]
    public void Plan_ConservativeProfile_UpgradesApsOneAtATime()
    {
        var devices = new List<PlannerDevice> { Gw(upgradable: false) };
        for (var i = 1; i <= 3; i++)
        {
            devices.Add(Ap($"aa:bb:cc:dd:ee:1{i}", $"AP-{i}", $"SKU-AP{i}", GatewayMac));
        }

        var doc = Plan(devices, Settings(profile: FirmwareSpacingProfile.Conservative)).Document;

        doc.Waves.Should().HaveCount(3);
        doc.Waves.Should().OnlyContain(w => w.Steps.Count == 1);
    }

    [Fact]
    public void Plan_MeshParticipants_AlwaysGetSoloWaves()
    {
        var meshParent = ApMac;
        var meshChild = "aa:bb:cc:dd:ee:05";
        var plainAp = "aa:bb:cc:dd:ee:06";
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac, upgradable: false),
            Ap(meshParent, "AP-1", "SKU-AP1", DistSwitchMac),
            Ap(plainAp, "AP-3", "SKU-AP3", DistSwitchMac),
            Ap(meshChild, "AP-2", "SKU-AP2", meshParent, wirelessUplink: true, meshInterface: "vwiresta0"),
        };

        var doc = Plan(devices).Document;

        doc.Waves.Should().OnlyContain(w => w.Steps.Count == 1);
        StepOf(doc, meshParent).IsMeshParticipant.Should().BeTrue();
        StepOf(doc, meshChild).IsMeshParticipant.Should().BeTrue();
        StepOf(doc, plainAp).IsMeshParticipant.Should().BeFalse();
    }

    [Fact]
    public void Plan_MeshParentStaysSolo_WhileOtherApsAtItsLevelPack()
    {
        var meshParent = ApMac;
        var meshChild = "aa:bb:cc:dd:ee:05";
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac, upgradable: false),
            Ap(meshParent, "AP-1", "SKU-AP1", DistSwitchMac),
            Ap("aa:bb:cc:dd:ee:06", "AP-3", "SKU-AP3", DistSwitchMac),
            Ap("aa:bb:cc:dd:ee:07", "AP-4", "SKU-AP4", DistSwitchMac),
            Ap(meshChild, "AP-2", "SKU-AP2", meshParent, wirelessUplink: true, meshInterface: "vwiresta0"),
        };

        var doc = Plan(devices).Document;

        WaveOf(doc, "aa:bb:cc:dd:ee:06").Should().Be(WaveOf(doc, "aa:bb:cc:dd:ee:07"));
        doc.Waves.Single(w => w.Number == WaveOf(doc, meshParent)).Steps.Should().ContainSingle();
    }

    // ---- Switch packing -------------------------------------------------------------

    [Fact]
    public void Plan_Switches_PackUpToMaxSwitchParallelism()
    {
        var devices = new List<PlannerDevice> { Gw(upgradable: false) };
        for (var i = 1; i <= 5; i++)
        {
            devices.Add(Sw($"aa:bb:cc:dd:ee:2{i}", $"Switch-{i}", $"SKU-SW{i}", GatewayMac));
        }

        var doc = Plan(devices).Document;

        doc.Waves.Select(w => w.Steps.Count).Should().Equal(2, 2, 1);
    }

    [Fact]
    public void Plan_SwitchParallelism_HonorsTheAdvancedOverride()
    {
        var devices = new List<PlannerDevice> { Gw(upgradable: false) };
        for (var i = 1; i <= 4; i++)
        {
            devices.Add(Sw($"aa:bb:cc:dd:ee:2{i}", $"Switch-{i}", $"SKU-SW{i}", GatewayMac));
        }

        var doc = Plan(devices, Settings(advancedSpacingJson: """{"maxSwitchParallelism":4}""")).Document;

        doc.Waves.Should().ContainSingle().Which.Steps.Should().HaveCount(4);
    }

    // ---- Mesh -----------------------------------------------------------------------

    [Fact]
    public void Plan_MeshChild_IsScheduledBeforeItsParent()
    {
        var meshParent = ApMac;
        var meshChild = "aa:bb:cc:dd:ee:05";
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(meshParent, "AP-1", "SKU-AP1", GatewayMac),
            Ap(meshChild, "AP-2", "SKU-AP2", meshParent, wirelessUplink: true, meshInterface: "vwiresta0"),
        };

        var doc = Plan(devices).Document;

        WaveOf(doc, meshChild).Should().BeLessThan(WaveOf(doc, meshParent));
    }

    [Fact]
    public void Plan_MeshRepair_CarriesChildDetailsAndRunsAfterBothEnds()
    {
        var meshParent = ApMac;
        var meshChild = "aa:bb:cc:dd:ee:05";
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(meshParent, "AP-1", "SKU-AP1", GatewayMac),
            Ap(meshChild, "AP-2", "SKU-AP2", meshParent, wirelessUplink: true, meshInterface: "vwiresta7",
                ipAddress: "192.0.2.20"),
        };

        var doc = Plan(devices).Document;

        var repair = doc.MeshRepairs.Should().ContainSingle().Subject;
        repair.ChildMac.Should().Be(meshChild);
        repair.ChildName.Should().Be("AP-2");
        repair.ChildIp.Should().Be("192.0.2.20");
        repair.ParentMac.Should().Be(meshParent);
        repair.Iface.Should().Be("vwiresta7");
        repair.AfterWave.Should().Be(Math.Max(WaveOf(doc, meshChild), WaveOf(doc, meshParent)));
        repair.AfterWave.Should().Be(WaveOf(doc, meshParent));
    }

    [Fact]
    public void Plan_MeshChildWhoseParentIsNotInThePlan_RepairsAfterItsOwnWave()
    {
        var meshParent = ApMac;
        var meshChild = "aa:bb:cc:dd:ee:05";
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(meshParent, "AP-1", "SKU-AP1", GatewayMac, upgradable: false),
            Ap(meshChild, "AP-2", "SKU-AP2", meshParent, wirelessUplink: true, meshInterface: "vwiresta7"),
        };

        var doc = Plan(devices).Document;

        var repair = doc.MeshRepairs.Should().ContainSingle().Subject;
        repair.AfterWave.Should().Be(WaveOf(doc, meshChild));
    }

    [Fact]
    public void Plan_MeshChildOnADifferentChannel_StillUpgradesBeforeItsParent()
    {
        var meshParent = ApMac;
        var meshChild = "aa:bb:cc:dd:ee:05";
        var devices = new[]
        {
            Ap(meshParent, "AP-1", "SKU-AP1"),
            Ap(meshChild, "AP-2", "SKU-AP2", meshParent, wirelessUplink: true, meshInterface: "vwiresta0"),
        };
        var settings = Settings(perSkuChannelsJson: """{"SKU-AP2":"beta"}""");

        var doc = Plan(devices, settings, currentConsoleChannel: FirmwareChannels.Release).Document;

        WaveOf(doc, meshChild).Should().BeLessThan(WaveOf(doc, meshParent));
    }

    [Fact]
    public void Plan_WirelessUplinkWithoutAMeshInterface_GetsNoRepair()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", ApMac, wirelessUplink: true),
        };

        Plan(devices).Document.MeshRepairs.Should().BeEmpty();
    }

    // ---- Exclusions -----------------------------------------------------------------

    [Fact]
    public void Plan_ExcludedByMac_IsASkippedStepOutsideEveryWave()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", GatewayMac),
        };
        var settings = Settings(exclusionsJson: """{"macs":["AA-BB-CC-DD-EE-04"]}""");

        var result = Plan(devices, settings);

        var skipped = result.Steps.Single(s => s.DeviceMac == ApMac);
        skipped.State.Should().Be(FirmwareRolloutStepState.SkippedExcluded);
        skipped.Wave.Should().Be(0);
        result.Document.Waves.SelectMany(w => w.Steps).Should().NotContain(s => s.Mac == ApMac);
        result.Document.Waves.SelectMany(w => w.Steps).Should().ContainSingle()
            .Which.Mac.Should().Be("aa:bb:cc:dd:ee:05");
    }

    [Fact]
    public void Plan_ExcludedBySku_IsSkipped()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", GatewayMac),
        };
        var settings = Settings(exclusionsJson: """{"skus":["SKU-AP2"]}""");

        var result = Plan(devices, settings);

        result.Steps.Single(s => s.DeviceMac == "aa:bb:cc:dd:ee:05").State
            .Should().Be(FirmwareRolloutStepState.SkippedExcluded);
        result.Document.Waves.SelectMany(w => w.Steps).Select(s => s.Mac).Should().Equal(ApMac);
    }

    [Fact]
    public void Plan_ExcludedByDeviceTypeCode_SkipsTheWholeType()
    {
        var devices = new[]
        {
            Gw(),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
        };
        var settings = Settings(exclusionsJson: """{"deviceTypes":["uap","ugw"]}""");

        var result = Plan(devices, settings);

        result.Steps.Where(s => s.State == FirmwareRolloutStepState.SkippedExcluded)
            .Select(s => s.DeviceMac).Should().BeEquivalentTo(new[] { ApMac, GatewayMac });
        result.Document.Waves.SelectMany(w => w.Steps).Select(s => s.Mac).Should().Equal(DistSwitchMac);
    }

    [Fact]
    public void Plan_ExcludedNonUpgradableDevice_ProducesNoStepAtAll()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac, upgradable: false),
        };
        var settings = Settings(exclusionsJson: """{"macs":["aa:bb:cc:dd:ee:04"]}""");

        Plan(devices, settings).Steps.Should().BeEmpty();
    }

    [Fact]
    public void Plan_ExcludedDeviceKeepsItsResolvedChannel_ForThePreview()
    {
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1") };
        var settings = Settings(
            perSkuChannelsJson: """{"SKU-AP1":"beta"}""",
            exclusionsJson: """{"macs":["aa:bb:cc:dd:ee:04"]}""");

        Plan(devices, settings).Steps.Single().Channel.Should().Be(FirmwareChannels.Beta);
    }

    // ---- Timeline -------------------------------------------------------------------

    [Fact]
    public void Plan_UniFiNetworkUpdate_TakesItsAllowanceBeforeTheFirstWave()
    {
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1") };

        var doc = Plan(devices, Settings(includeUniFiNetwork: true)).Document;

        doc.IncludesUniFiNetworkUpdate.Should().BeTrue();
        doc.UniFiNetworkUpdateSeconds.Should().Be(RolloutPlanner.UniFiNetworkUpdateSeconds);
        doc.Waves[0].StartOffsetSeconds.Should().Be(300);
        doc.TotalEstimatedSeconds.Should().Be(300 + 240 + RolloutPlanner.CommandOverheadSeconds);
    }

    [Fact]
    public void Plan_WithoutUniFiNetworkUpdate_StartsAtZero()
    {
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1") };

        var doc = Plan(devices, Settings(includeUniFiNetwork: false)).Document;

        doc.IncludesUniFiNetworkUpdate.Should().BeFalse();
        doc.UniFiNetworkUpdateSeconds.Should().Be(0);
        doc.Waves[0].StartOffsetSeconds.Should().Be(0);
    }

    [Fact]
    public void Plan_ChannelChange_IsChargedEnteringAndLeavingTheGroup()
    {
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1") };
        var settings = Settings(perSkuChannelsJson: """{"SKU-AP1":"beta"}""");

        var doc = Plan(devices, settings, currentConsoleChannel: FirmwareChannels.Release).Document;

        doc.ChannelGroups.Single().RequiresConsoleChange.Should().BeTrue();
        doc.Waves[0].StartOffsetSeconds.Should().Be(RolloutPlanner.ChannelChangeSeconds);
        doc.TotalEstimatedSeconds.Should().Be(
            RolloutPlanner.ChannelChangeSeconds + 240 + RolloutPlanner.CommandOverheadSeconds
            + RolloutPlanner.ChannelChangeSeconds);
    }

    [Fact]
    public void Plan_NoChannelChange_ChargesNoAllowance()
    {
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1") };

        var doc = Plan(devices, currentConsoleChannel: FirmwareChannels.Release).Document;

        doc.ChannelGroups.Single().RequiresConsoleChange.Should().BeFalse();
        doc.Waves[0].StartOffsetSeconds.Should().Be(0);
        doc.TotalEstimatedSeconds.Should().Be(240 + RolloutPlanner.CommandOverheadSeconds);
    }

    [Fact]
    public void Plan_WaveDuration_IsTheSlowestMemberPlusCommandOverhead()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac, upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac, displayModel: "U7 Pro"),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", DistSwitchMac, displayModel: "UAP-AC-Pro"),
            Ap("aa:bb:cc:dd:ee:06", "AP-3", "SKU-AP3", DistSwitchMac, displayModel: "U6 Pro"),
        };
        var settings = Settings(advancedSpacingJson: """{"maxApParallelism":2,"apGapSeconds":100}""");

        var doc = Plan(devices, settings).Document;

        doc.Waves[0].Steps.Should().HaveCount(2);
        doc.Waves[0].StartOffsetSeconds.Should().Be(0);
        // Slowest of the pair (the older AP at 420s), the command allowance, then the AP gap.
        doc.Waves[1].StartOffsetSeconds.Should().Be(420 + RolloutPlanner.CommandOverheadSeconds + 100);
        doc.TotalEstimatedSeconds.Should().Be(550 + 240 + RolloutPlanner.CommandOverheadSeconds);
    }

    [Fact]
    public void Plan_InterWaveGap_IsTheWavesOwnDeviceClassAndIsNotChargedAfterTheLastWave()
    {
        var devices = new[]
        {
            Gw(),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac),
        };

        var doc = Plan(devices).Document;

        doc.Waves[0].StartOffsetSeconds.Should().Be(0);
        // AP wave: 240 down + 30 overhead + 120 AP gap.
        doc.Waves[1].StartOffsetSeconds.Should().Be(390);
        // Switch wave: 480 down + 30 overhead + 180 switch gap.
        doc.Waves[2].StartOffsetSeconds.Should().Be(1080);
        // The gateway closes the only group, so its gateway gap is never charged.
        doc.TotalEstimatedSeconds.Should().Be(1080 + 300 + RolloutPlanner.CommandOverheadSeconds);
    }

    [Fact]
    public void Plan_WaveEtas_AreMonotonicAndMatchTheirSteps()
    {
        var devices = new List<PlannerDevice> { Gw() };
        for (var i = 1; i <= 4; i++)
        {
            devices.Add(Ap($"aa:bb:cc:dd:ee:1{i}", $"AP-{i}", $"SKU-AP{i}", GatewayMac));
        }
        for (var i = 1; i <= 3; i++)
        {
            devices.Add(Sw($"aa:bb:cc:dd:ee:2{i}", $"Switch-{i}", $"SKU-SW{i}", GatewayMac));
        }

        var doc = Plan(devices, Settings(includeUniFiNetwork: true)).Document;

        doc.Waves.Select(w => w.StartOffsetSeconds).Should().BeInAscendingOrder();
        foreach (var wave in doc.Waves)
        {
            wave.Steps.Should().OnlyContain(s => s.EtaOffsetSeconds == wave.StartOffsetSeconds);
        }
        doc.TotalEstimatedSeconds.Should().BeGreaterThan(doc.Waves.Last().StartOffsetSeconds);
    }

    [Fact]
    public void Plan_TotalEstimatedSeconds_ClosesOnTheLastWave()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", GatewayMac),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", GatewayMac),
        };

        var doc = Plan(devices).Document;

        var last = doc.Waves.Last();
        doc.TotalEstimatedSeconds.Should().Be(
            last.StartOffsetSeconds + last.Steps.Max(s => s.EstimatedDowntimeSeconds)
            + RolloutPlanner.CommandOverheadSeconds);
    }

    [Fact]
    public void Plan_LearnedTimings_FeedTheWaveEstimates()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "SKU-AP1", SampleCount = 6, MedianDowntimeSeconds = 195 }
        ]);
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1") };

        var doc = Plan(devices, estimator: estimator).Document;

        StepOf(doc, ApMac).EstimatedDowntimeSeconds.Should().Be(195);
        doc.TotalEstimatedSeconds.Should().Be(195 + RolloutPlanner.CommandOverheadSeconds);
    }

    // ---- Effective class ------------------------------------------------------------

    [Fact]
    public void Plan_CloudGatewayWithoutTheUniFiOsUpdate_IsBudgetedAsANetworkOnlyGateway()
    {
        var devices = new[] { Gw(model: "UDRULT", displayModel: "UCG-Ultra") };

        var doc = Plan(devices, Settings(includeUniFiOs: false)).Document;

        var step = StepOf(doc, GatewayMac);
        step.OfflineBudgetSeconds.Should().Be(FirmwareTimingEstimator.DefaultOfflineBudgetSeconds);
        step.EstimatedDowntimeSeconds.Should().Be(300);
        doc.IncludesUniFiOsUpdate.Should().BeFalse();
    }

    [Fact]
    public void Plan_CloudGatewayWithTheUniFiOsUpdate_GetsTheThirtyMinuteBudget()
    {
        var devices = new[] { Gw(model: "UDRULT", displayModel: "UCG-Ultra") };

        var doc = Plan(devices, Settings(includeUniFiOs: true)).Document;

        var step = StepOf(doc, GatewayMac);
        step.OfflineBudgetSeconds.Should().Be(FirmwareTimingEstimator.CloudGatewayOfflineBudgetSeconds);
        step.EstimatedDowntimeSeconds.Should().Be(1080);
        doc.IncludesUniFiOsUpdate.Should().BeTrue();
    }

    [Fact]
    public void Plan_NetworkOnlyGateway_IsNeverAUniFiOsUpdateEvenWhenTheOptionIsOn()
    {
        var devices = new[] { Gw(model: "UXGPRO", displayModel: "UXG-Pro") };

        var doc = Plan(devices, Settings(includeUniFiOs: true)).Document;

        StepOf(doc, GatewayMac).OfflineBudgetSeconds
            .Should().Be(FirmwareTimingEstimator.DefaultOfflineBudgetSeconds);
        doc.IncludesUniFiOsUpdate.Should().BeFalse();
    }

    [Fact]
    public void Plan_CarriesWhateverTargetTheGatherStaged_IncludingNone()
    {
        // The gather visits each planned channel and reads each device's target on its own, and
        // leaves it null for a channel it could not reach. The planner passes that through: it has
        // no better answer, and inventing one quotes a build from the wrong channel.
        var known = Ap(ApMac, "AP-1", "SKU-AP1");
        var unreachable = Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2");
        unreachable.ToVersion = null;

        var result = Plan([known, unreachable], Settings(globalChannel: FirmwareChannels.ReleaseCandidate),
            currentConsoleChannel: FirmwareChannels.Release);

        StepOf(result.Document, ApMac).ToVersion.Should().Be("1.1.0");
        StepOf(result.Document, "aa:bb:cc:dd:ee:05").ToVersion.Should().BeNull();
        result.Document.ChannelGroups.Should().ContainSingle()
            .Which.RequiresConsoleChange.Should().BeTrue();
    }

    [Fact]
    public void Plan_CloudGateway_NamesItselfAsTheConsole()
    {
        var devices = new[] { Gw(model: "UDRULT", displayModel: "UCG-Ultra") };

        var doc = Plan(devices, Settings(includeUniFiOs: true)).Document;

        doc.ConsoleMac.Should().Be(GatewayMac);
    }

    [Fact]
    public void Plan_CloudGateway_IsStillTheConsoleWithTheUniFiOsUpdateTurnedOff()
    {
        var devices = new[] { Gw(model: "UDRULT", displayModel: "UCG-Ultra") };

        // The UniFi Network application still installs on it, so the map has a node to mark.
        var doc = Plan(devices, Settings(includeUniFiOs: false)).Document;

        doc.ConsoleMac.Should().Be(GatewayMac);
    }

    [Fact]
    public void Plan_NetworkOnlyGateway_IsNotTheConsole()
    {
        var devices = new[] { Gw(model: "UXGPRO", displayModel: "UXG-Pro") };

        var doc = Plan(devices, Settings(includeUniFiOs: true)).Document;

        doc.ConsoleMac.Should().BeNull();
    }

    [Fact]
    public void Plan_AccessPointsAndSwitches_CarryTheStandardBudget()
    {
        var devices = new[]
        {
            Ap(ApMac, "AP-1", "SKU-AP1"),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1"),
        };

        var doc = Plan(devices, Settings(includeUniFiOs: true)).Document;

        doc.Waves.SelectMany(w => w.Steps)
            .Should().OnlyContain(s => s.OfflineBudgetSeconds == FirmwareTimingEstimator.DefaultOfflineBudgetSeconds);
    }

    // ---- Steps and metadata ---------------------------------------------------------

    [Fact]
    public void Plan_Steps_CarryTheDeviceFactsTheExecutorNeeds()
    {
        var devices = new[] { Ap(ApMac, "AP-1", "SKU-AP1", displayModel: "U7 Pro") };

        // Same channel the console is already on, so this exercises the facts, not the
        // channel-change path that deliberately withholds a target.
        var result = Plan(devices, Settings(globalChannel: FirmwareChannels.ReleaseCandidate),
            currentConsoleChannel: FirmwareChannels.ReleaseCandidate);

        var step = result.Steps.Should().ContainSingle().Subject;
        step.DeviceMac.Should().Be(ApMac);
        step.DeviceName.Should().Be("AP-1");
        step.Model.Should().Be("SKU-AP1");
        step.DeviceType.Should().Be("uap");
        step.FromVersion.Should().Be("1.0.0");
        step.ToVersion.Should().Be("1.1.0");
        step.Channel.Should().Be(FirmwareChannels.ReleaseCandidate);
        step.Wave.Should().Be(1);
        step.State.Should().Be(FirmwareRolloutStepState.Pending);

        var planStep = result.Document.Waves[0].Steps[0];
        planStep.Name.Should().Be("AP-1");
        planStep.DisplayModel.Should().Be("U7 Pro");
        planStep.DeviceType.Should().Be("uap");
    }

    [Fact]
    public void Plan_UnnamedDevice_FallsBackToItsModelNameOnTheStep()
    {
        var devices = new[] { Ap(ApMac, name: "", model: "SKU-AP1", displayModel: "U7 Pro") };

        Plan(devices).Steps.Single().DeviceName.Should().Be("U7 Pro");
    }

    [Fact]
    public void Plan_EveryCandidate_AppearsExactlyOnceAcrossTheWaves()
    {
        var devices = new List<PlannerDevice> { Gw() };
        for (var i = 1; i <= 5; i++)
        {
            devices.Add(Ap($"aa:bb:cc:dd:ee:1{i}", $"AP-{i}", "SKU-AP1", GatewayMac));
        }
        for (var i = 1; i <= 3; i++)
        {
            devices.Add(Sw($"aa:bb:cc:dd:ee:2{i}", $"Switch-{i}", "SKU-SW1", GatewayMac));
        }

        var result = Plan(devices);

        var planned = result.Document.Waves.SelectMany(w => w.Steps).Select(s => s.Mac).ToList();
        planned.Should().OnlyHaveUniqueItems();
        planned.Should().HaveCount(9);
        result.Steps.Select(s => s.DeviceMac).Should().BeEquivalentTo(planned);
    }

    // ---- Notes ----------------------------------------------------------------------

    [Fact]
    public void Plan_NoNeighborOracle_SaysSoInTheNotes()
    {
        var doc = Plan([Ap(ApMac, "AP-1", "SKU-AP1")]).Document;

        doc.Notes.Should().Contain(n => n.Contains("uniform AP density"));
    }

    [Fact]
    public void Plan_OracleWithoutPlacements_SaysRoamingNeighborsWereUsed()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: false);

        var doc = Plan([Ap(ApMac, "AP-1", "SKU-AP1")], neighbors: oracle).Document;

        doc.Notes.Should().Contain(n => n.Contains("roaming neighbors"));
        doc.Notes.Should().NotContain(n => n.Contains("uniform AP density"));
    }

    [Fact]
    public void Plan_GatewayNote_CallsOutTheUniFiOsCycleWhenIncluded()
    {
        var doc = Plan([Gw(model: "UDRULT", displayModel: "UCG-Ultra")], Settings(includeUniFiOs: true)).Document;

        doc.Notes.Should().Contain(n => n.Contains("30 minutes"));
    }

    [Fact]
    public void Plan_GatewayNote_IsTheShortOneWithoutTheUniFiOsCycle()
    {
        var doc = Plan([Gw(model: "UDRULT", displayModel: "UCG-Ultra")], Settings(includeUniFiOs: false)).Document;

        doc.Notes.Should().Contain(n => n.Contains("briefly unreachable"));
        doc.Notes.Should().NotContain(n => n.Contains("30 minutes"));
    }

    [Fact]
    public void Plan_MeshNote_AppearsOnlyWhenRepairsAreQueued()
    {
        var withoutMesh = Plan([Ap(ApMac, "AP-1", "SKU-AP1")]).Document;
        withoutMesh.Notes.Should().NotContain(n => n.Contains("backhaul re-scan"));

        var devices = new[]
        {
            Ap(ApMac, "AP-1", "SKU-AP1"),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2", ApMac, wirelessUplink: true, meshInterface: "vwiresta0"),
        };
        Plan(devices).Document.Notes.Should().Contain(n => n.Contains("backhaul re-scan"));
    }

    // ---- Empty inputs ---------------------------------------------------------------

    [Fact]
    public void Plan_NoDevices_ProducesAnEmptyPlan()
    {
        var result = Plan([]);

        result.Document.Waves.Should().BeEmpty();
        result.Document.ChannelGroups.Should().BeEmpty();
        result.Document.MeshRepairs.Should().BeEmpty();
        result.Document.TotalEstimatedSeconds.Should().Be(0);
        result.Steps.Should().BeEmpty();
    }

    [Fact]
    public void Plan_NothingUpgradable_ProducesAnEmptyPlan()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Sw(DistSwitchMac, "Switch-1", "SKU-SW1", GatewayMac, upgradable: false),
            Ap(ApMac, "AP-1", "SKU-AP1", DistSwitchMac, upgradable: false),
        };

        var result = Plan(devices);

        result.Document.Waves.Should().BeEmpty();
        result.Document.TotalEstimatedSeconds.Should().Be(0);
        result.Steps.Should().BeEmpty();
    }

    [Fact]
    public void Plan_EverythingExcluded_LeavesNoWavesButKeepsThePreviewRows()
    {
        var devices = new[]
        {
            Ap(ApMac, "AP-1", "SKU-AP1"),
            Ap("aa:bb:cc:dd:ee:05", "AP-2", "SKU-AP2"),
        };
        var settings = Settings(exclusionsJson: """{"deviceTypes":["uap"]}""");

        var result = Plan(devices, settings);

        result.Document.Waves.Should().BeEmpty();
        result.Document.TotalEstimatedSeconds.Should().Be(0);
        result.Steps.Should().HaveCount(2)
            .And.OnlyContain(s => s.State == FirmwareRolloutStepState.SkippedExcluded);
    }

    [Fact]
    public void Plan_TheSameApInTwoColors_StillGetsACanary()
    {
        // UAPA6A9 and UAPA6AE are a U7-Pro-XG and the black one: same hardware, same firmware,
        // different console codes. Counted apart they were two models of one device each, so
        // neither earned a canary and both went out with nothing gating them.
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-White", "UAPA6A9", GatewayMac),
            Ap("aa:bb:cc:dd:ee:07", "AP-Black", "UAPA6AE", GatewayMac),
        };

        var result = Plan(devices);

        var canaries = result.Document.Waves.SelectMany(w => w.Steps).Where(s => s.IsCanary).ToList();
        canaries.Should().ContainSingle();
        result.Document.Waves.SelectMany(w => w.Steps)
            .Single(s => s.Mac != canaries[0].Mac && s.Mac != GatewayMac)
            .HeldForCanary.Should().BeTrue();
    }

    [Fact]
    public void Plan_ExcludingAModel_TakesItsOtherColorToo()
    {
        var devices = new[]
        {
            Gw(upgradable: false),
            Ap(ApMac, "AP-White", "UAPA6A9", GatewayMac),
            Ap("aa:bb:cc:dd:ee:07", "AP-Black", "UAPA6AE", GatewayMac),
        };
        var settings = Settings();
        settings.ExclusionsJson = """{"macs":[],"skus":["UAPA6A9"],"deviceTypes":[]}""";

        var result = Plan(devices, settings);

        result.Steps.Where(s => s.DeviceMac != GatewayMac)
            .Should().OnlyContain(s => s.State == FirmwareRolloutStepState.SkippedExcluded);
    }
}
