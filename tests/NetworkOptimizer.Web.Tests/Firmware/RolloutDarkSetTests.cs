using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// What a rebooting device takes dark, per site shape. The shapes are the ones deployments
/// actually come in: a Cloud Gateway that is its own console, a self-hosted console on a switch,
/// and the places a probe vantage can sit (this server on a switch, an agent on the gateway, an
/// agent on a switch). Every assertion is the set a live incident would have needed.
/// </summary>
public class RolloutDarkSetTests
{
    private const string Gateway = "aa:bb:cc:00:00:01";
    private const string Core = "aa:bb:cc:00:00:02";
    private const string Leaf = "aa:bb:cc:00:00:03";
    private const string ApOnGateway = "aa:bb:cc:00:00:04";
    private const string ApOnCore = "aa:bb:cc:00:00:05";
    private const string ApOnLeaf = "aa:bb:cc:00:00:06";
    private const string MeshChild = "aa:bb:cc:00:00:07";
    private const string SwitchBehindMesh = "aa:bb:cc:00:00:08";

    private static RolloutDeviceObservation Device(string mac, string? uplink, bool gateway = false, bool wireless = false, string? lastUplink = null) =>
        new()
        {
            Mac = mac,
            Name = mac,
            Model = gateway ? "UXGPRO" : "USW",
            UplinkMac = uplink,
            LastUplinkMac = lastUplink,
            IsGateway = gateway,
            WirelessUplink = wireless,
        };

    /// <summary>
    /// gateway - core - leaf - AP; an AP on the gateway's own port, an AP on the core, and a mesh
    /// AP off that one with a switch hanging off the mesh AP. On a self-hosted console the
    /// gateway's LAN side is known only from last_uplink, which points at the core.
    /// </summary>
    private static List<RolloutDeviceObservation> Site(bool cloudGateway) =>
    [
        Device(Gateway, uplink: null, gateway: true, lastUplink: cloudGateway ? null : Core),
        Device(Core, Gateway),
        Device(Leaf, Core),
        Device(ApOnGateway, Gateway),
        Device(ApOnCore, Core),
        Device(ApOnLeaf, Leaf),
        Device(MeshChild, ApOnCore, wireless: true),
        Device(SwitchBehindMesh, MeshChild),
    ];

    private static RolloutObserverPositions ConsoleOn(string attach) =>
        new(attach, false, new Dictionary<string, string?>());

    // --- Cloud Gateway console: the console is the root, so dark = the subtree ---------------

    [Fact]
    public void CloudGateway_CoreSwitchStep_DarkensItsSubtreeAndTheMeshChildren_NotTheGatewaysOwnPorts()
    {
        var dark = RolloutDarkSet.DevicesDarkenedBy(Core, true, Site(cloudGateway: true), RolloutObserverPositions.ConsoleAtRoot);

        dark.Should().BeEquivalentTo([Leaf, ApOnLeaf, ApOnCore, MeshChild, SwitchBehindMesh]);
        dark.Should().NotContain(ApOnGateway);
        dark.Should().NotContain(Gateway);
    }

    [Fact]
    public void CloudGateway_LeafSwitchStep_DarkensItsSubtree_PlusMeshChildrenAnywhere()
    {
        var dark = RolloutDarkSet.DevicesDarkenedBy(Leaf, true, Site(cloudGateway: true), RolloutObserverPositions.ConsoleAtRoot);

        // The wired reconvergence when the leaf returns drops the mesh backhaul off the core AP.
        dark.Should().BeEquivalentTo([ApOnLeaf, MeshChild, SwitchBehindMesh]);
    }

    [Fact]
    public void CloudGateway_AccessPointStep_DarkensOnlyWhatHangsOffIt()
    {
        var dark = RolloutDarkSet.DevicesDarkenedBy(ApOnCore, false, Site(cloudGateway: true), RolloutObserverPositions.ConsoleAtRoot);

        dark.Should().BeEquivalentTo([MeshChild, SwitchBehindMesh]);
    }

    // --- Self-hosted console on a switch: dark = whatever's path to that switch crosses the step

    [Fact]
    public void SelfHostedConsoleOnCore_CoreSwitchStep_DarkensEverythingIncludingTheGatewaysOwnPorts()
    {
        var dark = RolloutDarkSet.DevicesDarkenedBy(Core, true, Site(cloudGateway: false), ConsoleOn(Core));

        dark.Should().BeEquivalentTo([Gateway, Leaf, ApOnGateway, ApOnCore, ApOnLeaf, MeshChild, SwitchBehindMesh]);
    }

    [Fact]
    public void SelfHostedConsoleOnCore_LeafSwitchStep_DarkensOnlyItsSubtreeAndTheMeshChildren()
    {
        var dark = RolloutDarkSet.DevicesDarkenedBy(Leaf, true, Site(cloudGateway: false), ConsoleOn(Core));

        dark.Should().BeEquivalentTo([ApOnLeaf, MeshChild, SwitchBehindMesh]);
        dark.Should().NotContain(ApOnGateway);
        dark.Should().NotContain(Gateway);
    }

    [Fact]
    public void SelfHostedConsoleOnLeaf_CoreSwitchStep_DarkensEverythingButTheLeafsOwnSubtree()
    {
        // The console hangs off the leaf: the leaf and the AP on it still reach it, nothing else does.
        var dark = RolloutDarkSet.DevicesDarkenedBy(Core, true, Site(cloudGateway: false), ConsoleOn(Leaf));

        dark.Should().BeEquivalentTo([Gateway, ApOnGateway, ApOnCore, MeshChild, SwitchBehindMesh]);
        dark.Should().NotContain(Leaf);
        dark.Should().NotContain(ApOnLeaf);
    }

    [Fact]
    public void TheConsolesOwnSwitchRebooting_DarkensEverything()
    {
        var dark = RolloutDarkSet.DevicesDarkenedBy(Leaf, true, Site(cloudGateway: false), ConsoleOn(Leaf));

        dark.Should().HaveCount(7).And.NotContain(Leaf);
    }

    [Fact]
    public void SelfHostedConsoleNobodyCouldPlace_InfrastructureStepDarkensEverything_AccessPointStepDoesNot()
    {
        var unlocated = new RolloutObserverPositions(null, true, new Dictionary<string, string?>());

        RolloutDarkSet.DevicesDarkenedBy(Leaf, true, Site(cloudGateway: false), unlocated)
            .Should().HaveCount(7).And.NotContain(Leaf);
        RolloutDarkSet.DevicesDarkenedBy(ApOnLeaf, false, Site(cloudGateway: false), unlocated)
            .Should().BeEmpty();
    }

    [Fact]
    public void TheGatewaysLastUplinkNeverClosesACycle()
    {
        var parents = RolloutDarkSet.ParentMap(Site(cloudGateway: false));

        parents[Gateway].Should().BeNull();
        RolloutDarkSet.ChainToRoot(ApOnLeaf, parents).Should().Equal(ApOnLeaf, Leaf, Core, Gateway);
        RolloutDarkSet.PathBetween(ApOnGateway, Core, parents).Should().Equal(ApOnGateway, Gateway, Core);
    }

    [Fact]
    public void AnOfflineDeviceKeepsItsPlaceThroughLastUplink()
    {
        var site = Site(cloudGateway: true);
        site[site.FindIndex(d => d.Mac == ApOnLeaf)] = Device(ApOnLeaf, uplink: null, lastUplink: Leaf);

        RolloutDarkSet.DevicesDarkenedBy(Leaf, true, site, RolloutObserverPositions.ConsoleAtRoot)
            .Should().Contain(ApOnLeaf);
    }

    [Fact]
    public void ADeviceWhoseUplinkIsOffTheMapIsDarkOnlyToItsOwnAncestors()
    {
        // Rebooting a device is never a reason to count it dark for itself.
        var dark = RolloutDarkSet.DevicesDarkenedBy(Leaf, true, Site(cloudGateway: true), RolloutObserverPositions.ConsoleAtRoot);

        dark.Should().NotContain(Leaf);
    }

    // --- Vantages: WAN probes are dark when the step sits between the vantage and the gateway --

    [Fact]
    public void ServerOnTheCore_IsCutOffByTheCore_NotByALeaf()
    {
        var parents = RolloutDarkSet.ParentMap(Site(cloudGateway: false));

        RolloutDarkSet.VantageDarkenedBy(Core, true, Core, Gateway, parents).Should().BeTrue();
        RolloutDarkSet.VantageDarkenedBy(Leaf, true, Core, Gateway, parents).Should().BeFalse();
    }

    [Fact]
    public void ServerOnTheLeaf_IsCutOffByTheLeafAndTheCore()
    {
        var parents = RolloutDarkSet.ParentMap(Site(cloudGateway: true));

        RolloutDarkSet.VantageDarkenedBy(Leaf, true, Leaf, Gateway, parents).Should().BeTrue();
        RolloutDarkSet.VantageDarkenedBy(Core, true, Leaf, Gateway, parents).Should().BeTrue();
        RolloutDarkSet.VantageDarkenedBy(ApOnCore, false, Leaf, Gateway, parents).Should().BeFalse();
    }

    [Fact]
    public void AnAgentOnTheGateway_IsNeverCutOffByASwitch()
    {
        var parents = RolloutDarkSet.ParentMap(Site(cloudGateway: true));

        RolloutDarkSet.VantageDarkenedBy(Core, true, Gateway, Gateway, parents).Should().BeFalse();
        RolloutDarkSet.VantageDarkenedBy(Leaf, true, Gateway, Gateway, parents).Should().BeFalse();
        RolloutDarkSet.VantageDarkenedBy(Gateway, true, Gateway, Gateway, parents).Should().BeTrue();
    }

    [Fact]
    public void AVantageNobodyCouldPlace_IsCutOffByAnyInfrastructureStep_NotByAnAccessPoint()
    {
        var parents = RolloutDarkSet.ParentMap(Site(cloudGateway: true));

        RolloutDarkSet.VantageDarkenedBy(Leaf, true, null, Gateway, parents).Should().BeTrue();
        RolloutDarkSet.VantageDarkenedBy(ApOnLeaf, false, null, Gateway, parents).Should().BeFalse();
    }

    [Fact]
    public void AVantageBehindAMeshChild_IsCutOffByTheAccessPointItMeshesTo()
    {
        var parents = RolloutDarkSet.ParentMap(Site(cloudGateway: true));

        RolloutDarkSet.VantageDarkenedBy(ApOnCore, false, SwitchBehindMesh, Gateway, parents).Should().BeTrue();
        RolloutDarkSet.VantageDarkenedBy(Leaf, true, SwitchBehindMesh, Gateway, parents).Should().BeFalse();
    }
}
