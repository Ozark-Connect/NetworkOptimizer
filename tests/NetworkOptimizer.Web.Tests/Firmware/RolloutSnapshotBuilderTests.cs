using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The freeze step between live discovery and the pure planner. Two details carry weight: a
/// wireless uplink is only a mesh backhaul when the interface is a vwiresta STA (a gateway's
/// WAN uplink reports wireless too), and devices the console cannot command - unadopted, or
/// with no MAC to command - never reach a plan.
/// </summary>
public class RolloutSnapshotBuilderTests
{
    private static DiscoveredDevice Discovered(
        string mac = "AA-BB-CC-DD-EE-01",
        string name = "AP-1",
        DeviceType type = DeviceType.AccessPoint,
        string model = "SKU-AP1",
        bool adopted = true,
        bool upgradable = true) => new()
        {
            Mac = mac,
            Name = name,
            Type = type,
            Model = model,
            Adopted = adopted,
            Upgradable = upgradable,
        };

    [Fact]
    public void FromDevices_MapsEveryFieldThePlannerReads()
    {
        var device = Discovered();
        device.Firmware = "1.0.0";
        device.UpgradeToFirmware = "1.1.0";
        device.UplinkMac = "AABBCCDDEE02";
        device.UplinkPort = 5;
        device.LocalUplinkPort = 1;
        device.UplinkType = "wire";
        device.IpAddress = "192.0.2.10";

        var planner = RolloutSnapshotBuilder.FromDevices([device]).Should().ContainSingle().Subject;

        planner.Mac.Should().Be("aa:bb:cc:dd:ee:01");
        planner.Name.Should().Be("AP-1");
        planner.Model.Should().Be("SKU-AP1");
        planner.DisplayModel.Should().Be("SKU-AP1");
        planner.Type.Should().Be(DeviceType.AccessPoint);
        planner.Upgradable.Should().BeTrue();
        planner.FromVersion.Should().Be("1.0.0");
        planner.ToVersion.Should().Be("1.1.0");
        planner.UplinkMac.Should().Be("aa:bb:cc:dd:ee:02");
        planner.UplinkRemotePort.Should().Be(5);
        planner.UplinkLocalPort.Should().Be(1);
        planner.WirelessUplink.Should().BeFalse();
        planner.MeshUplinkInterface.Should().BeNull();
        planner.IpAddress.Should().Be("192.0.2.10");
    }

    [Fact]
    public void FromDevices_UnnamedDevice_FallsBackToTheFriendlyModelName()
    {
        var device = Discovered(name: "", model: "SKU-AP1");

        RolloutSnapshotBuilder.FromDevices([device]).Single().Name.Should().Be("SKU-AP1");
    }

    [Fact]
    public void FromDevices_EmptyFirmware_BecomesNull()
    {
        var device = Discovered();
        device.Firmware = "";

        RolloutSnapshotBuilder.FromDevices([device]).Single().FromVersion.Should().BeNull();
    }

    [Fact]
    public void FromDevices_GatewayLanIp_WinsOverTheStandardIp()
    {
        var device = Discovered(type: DeviceType.Gateway, model: "SKU-GW1");
        device.IpAddress = "192.0.2.1";
        device.LanIpAddress = "192.0.2.254";

        RolloutSnapshotBuilder.FromDevices([device]).Single().IpAddress.Should().Be("192.0.2.254");
    }

    [Fact]
    public void FromDevices_NoIpAtAll_BecomesNull()
    {
        RolloutSnapshotBuilder.FromDevices([Discovered()]).Single().IpAddress.Should().BeNull();
    }

    [Fact]
    public void FromDevices_NoUplink_LeavesTheParentNull()
    {
        var device = Discovered(type: DeviceType.Gateway, model: "SKU-GW1");
        device.UplinkMac = "";

        RolloutSnapshotBuilder.FromDevices([device]).Single().UplinkMac.Should().BeNull();
    }

    [Fact]
    public void FromDevices_MeshBackhaulUplink_CarriesTheStaInterface()
    {
        var device = Discovered();
        device.UplinkType = "wireless";
        device.UplinkInterface = "vwiresta7";

        var planner = RolloutSnapshotBuilder.FromDevices([device]).Single();

        planner.WirelessUplink.Should().BeTrue();
        planner.MeshUplinkInterface.Should().Be("vwiresta7");
    }

    [Fact]
    public void FromDevices_WirelessUplinkOnAnotherInterface_IsNotAMeshBackhaul()
    {
        var device = Discovered();
        device.UplinkType = "wireless";
        device.UplinkInterface = "wlan0";

        var planner = RolloutSnapshotBuilder.FromDevices([device]).Single();

        planner.WirelessUplink.Should().BeTrue();
        planner.MeshUplinkInterface.Should().BeNull();
    }

    [Fact]
    public void FromDevices_WiredUplinkNamedLikeAStaInterface_IsNotAMeshBackhaul()
    {
        var device = Discovered();
        device.UplinkType = "wire";
        device.UplinkInterface = "vwiresta7";

        var planner = RolloutSnapshotBuilder.FromDevices([device]).Single();

        planner.WirelessUplink.Should().BeFalse();
        planner.MeshUplinkInterface.Should().BeNull();
    }

    [Fact]
    public void FromDevices_WirelessUplinkWithNoInterfaceName_IsNotAMeshBackhaul()
    {
        var device = Discovered();
        device.UplinkType = "wireless";

        RolloutSnapshotBuilder.FromDevices([device]).Single().MeshUplinkInterface.Should().BeNull();
    }

    [Fact]
    public void FromDevices_UplinkTypeCasing_StillReadsAsWireless()
    {
        var device = Discovered();
        device.UplinkType = "Wireless";
        device.UplinkInterface = "VWIRESTA3";

        var planner = RolloutSnapshotBuilder.FromDevices([device]).Single();

        planner.WirelessUplink.Should().BeTrue();
        planner.MeshUplinkInterface.Should().Be("VWIRESTA3");
    }

    [Fact]
    public void FromDevices_UnadoptedDevice_IsSkipped()
    {
        var devices = new[]
        {
            Discovered(mac: "aa:bb:cc:dd:ee:01", adopted: true),
            Discovered(mac: "aa:bb:cc:dd:ee:02", name: "AP-2", adopted: false),
        };

        RolloutSnapshotBuilder.FromDevices(devices).Select(d => d.Mac).Should().Equal("aa:bb:cc:dd:ee:01");
    }

    [Fact]
    public void FromDevices_DeviceWithNoMac_IsSkipped()
    {
        var devices = new[]
        {
            Discovered(mac: ""),
            Discovered(mac: "aa:bb:cc:dd:ee:02", name: "AP-2"),
        };

        RolloutSnapshotBuilder.FromDevices(devices).Select(d => d.Mac).Should().Equal("aa:bb:cc:dd:ee:02");
    }

    [Fact]
    public void FromDevices_NonUpgradableDevices_AreKeptForTopologyDepth()
    {
        var devices = new[]
        {
            Discovered(mac: "aa:bb:cc:dd:ee:01", upgradable: false),
            Discovered(mac: "aa:bb:cc:dd:ee:02", name: "AP-2", upgradable: true),
        };

        var snapshot = RolloutSnapshotBuilder.FromDevices(devices);

        snapshot.Should().HaveCount(2);
        snapshot[0].Upgradable.Should().BeFalse();
    }

    [Fact]
    public void FromDevices_NoDevices_ReturnsEmpty()
    {
        RolloutSnapshotBuilder.FromDevices([]).Should().BeEmpty();
    }
}

/// <summary>
/// The neighbor set the AP-parallelism rule asks. Pairs go in from two different sources
/// (propagation and roaming edges) in whatever MAC spelling those carry, so the set has to be
/// order- and format-blind, and an AP is never its own neighbor - that would strand it in a
/// wave of one forever.
/// </summary>
public class ApNeighborOracleTests
{
    private const string MacA = "aa:bb:cc:dd:ee:01";
    private const string MacB = "aa:bb:cc:dd:ee:02";
    private const string MacC = "aa:bb:cc:dd:ee:03";

    [Fact]
    public void AreNeighbors_IsSymmetric()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors(MacA, MacB);

        oracle.AreNeighbors(MacA, MacB).Should().BeTrue();
        oracle.AreNeighbors(MacB, MacA).Should().BeTrue();
    }

    [Fact]
    public void AreNeighbors_IgnoresMacFormatting()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors("AA-BB-CC-DD-EE-01", "aabbccddee02");

        oracle.AreNeighbors(MacA, MacB).Should().BeTrue();
        oracle.AreNeighbors("AA:BB:CC:DD:EE:02", "AABBCCDDEE01").Should().BeTrue();
    }

    [Fact]
    public void AreNeighbors_UnrelatedPair_IsFalse()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors(MacA, MacB);

        oracle.AreNeighbors(MacA, MacC).Should().BeFalse();
        oracle.AreNeighbors(MacC, MacB).Should().BeFalse();
    }

    [Fact]
    public void AreNeighbors_SamePairInEitherOrder_IsStoredOnce()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors(MacA, MacB);
        oracle.AddNeighbors(MacB, MacA);

        oracle.AreNeighbors(MacA, MacB).Should().BeTrue();
    }

    [Fact]
    public void AreNeighbors_SelfPair_IsNeverANeighbor()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors(MacA, MacA);

        oracle.AreNeighbors(MacA, MacA).Should().BeFalse();
        oracle.AreNeighbors(MacA, "AA-BB-CC-DD-EE-01").Should().BeFalse();
    }

    [Fact]
    public void AreNeighbors_EmptyMac_IsNeverANeighbor()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: true);
        oracle.AddNeighbors("", MacB);

        oracle.AreNeighbors("", MacB).Should().BeFalse();
    }

    [Fact]
    public void HasPlacementData_ReportsHowTheOracleWasBuilt()
    {
        new ApNeighborOracle(hasPlacementData: true).HasPlacementData.Should().BeTrue();
        new ApNeighborOracle(hasPlacementData: false).HasPlacementData.Should().BeFalse();
    }

    [Fact]
    public void AreNeighbors_EmptyOracle_SaysNoToEverything()
    {
        var oracle = new ApNeighborOracle(hasPlacementData: false);

        oracle.AreNeighbors(MacA, MacB).Should().BeFalse();
    }
}
