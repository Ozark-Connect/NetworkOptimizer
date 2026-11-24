using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>Topology to links: fabric (wired and mesh), access ports, names, clients behind, and the swap neighbour.</summary>
public class LanLinkBuilderTests
{
    private const string Gw = "aa:bb:cc:00:00:01", Sw = "aa:bb:cc:00:00:02", Ap = "aa:bb:cc:00:00:03", Mesh = "aa:bb:cc:00:00:04", Hidden = "aa:bb:cc:00:00:05";
    private const string Pc = "aa:bb:cc:00:01:01", Nas = "aa:bb:cc:00:01:02", Phone = "aa:bb:cc:00:01:03", Tv1 = "aa:bb:cc:00:01:04", Tv2 = "aa:bb:cc:00:01:05";

    private static SwitchPort Port(int idx, bool up = true, int speed = 1000, string? lastMac = null, long? lastSeen = null, bool isUplink = false, string? name = null) => new()
    {
        PortIdx = idx, IfName = $"eth{idx - 1}", Up = up, Enable = true, Speed = speed, FullDuplex = true, Autoneg = true, IsUplink = isUplink, Name = name ?? "",
        LastConnection = lastMac == null ? null : new PortLastConnection { Mac = lastMac, LastSeen = lastSeen },
    };

    private static List<UniFiDeviceResponse> Site()
    {
        var recent = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        var old = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        return
        [
            new() { Mac = Gw, Name = "Gateway A", Type = "uxg", Model = "UXGA6AA", Adopted = true,
                PortTable = [Port(1), Port(3), Port(4, up: false, lastMac: Pc, lastSeen: old), Port(5, isUplink: true), Port(6, up: false, lastMac: Hidden, lastSeen: recent)] },
            new() { Mac = Sw, Name = "Switch A", Type = "usw", Model = "US24", Adopted = true,
                Uplink = new UplinkInfo { Type = "wire", UplinkMac = Gw, UplinkRemotePort = 1, PortIdx = 24, Speed = 1000, FullDuplex = true },
                PortTable = [Port(4), Port(5, speed: 2500), Port(6, up: false, lastMac: Nas, lastSeen: recent), Port(7), Port(24)] },
            new() { Mac = Ap, Name = "AP 1", Type = "uap", Model = "U7PRO", Adopted = true,
                Uplink = new UplinkInfo { Type = "wire", UplinkMac = Sw, UplinkRemotePort = 5, PortIdx = 1, Speed = 2500, FullDuplex = true } },
            new() { Mac = Mesh, Name = "AP 2", Type = "uap", Model = "U7PRO", Adopted = true,
                Uplink = new UplinkInfo { Type = "wireless", UplinkMac = Ap, Signal = -58, TxRate = 866_000 } },
            new() { Mac = Hidden, Name = "Switch Mini", Type = "usw", Model = "USMINI", Adopted = true },
        ];
    }

    private static List<LanLinkBuilder.PortClient> Clients() =>
    [
        new(Phone, "Phone", null, null, Mesh, false),
        new(Pc, "Gaming PC", Gw, 3, null, true),
        new(Tv1, "TV 1", Sw, 7, null, true),
        new(Tv2, "TV 2", Sw, 7, null, true),
    ];

    private static List<LinkRecord> Build() =>
        LanLinkBuilder.Build(Site(), Clients(), offlineNames: new Dictionary<string, string> { [Nas] = "NAS (offline name)" });

    [Fact]
    public void FabricLinks_WiredAndMesh()
    {
        var links = Build();
        var gwSw = links.Single(l => l.Id == LanLinkBuilder.WiredId(Gw, 1, Sw, 24));
        gwSw.Kind.Should().Be(LinkKind.FabricWired);
        gwSw.Name.Should().Be("Gateway A Port 1 to Switch A");
        gwSw.ClientsBehind.Should().Be(3, "the phone on the mesh AP and the two TVs; the PC is on the gateway");
        gwSw.DevicesBehind.Should().Be(3);

        var mesh = links.Single(l => l.Id == LanLinkBuilder.MeshId(Ap, Mesh));
        mesh.Kind.Should().Be(LinkKind.FabricMesh);
        mesh.Name.Should().Be("AP 1 to AP 2");
        mesh.MeshSignalDbm.Should().Be(-58);
        mesh.SpeedMbps.Should().Be(866);
    }

    [Fact]
    public void AccessPorts_NameTheirClient_OrTheCount_OrTheOfflineName()
    {
        var access = Build().Where(l => l.Kind == LinkKind.Access).ToDictionary(l => l.Id);
        access[LanLinkBuilder.AccessId(Gw, 3)].Downstream.DeviceName.Should().Be("Gaming PC");
        access[LanLinkBuilder.AccessId(Sw, 7)].Downstream.DeviceName.Should().Be("2 clients");
        access[LanLinkBuilder.AccessId(Sw, 7)].ClientsBehind.Should().Be(2);
        access[LanLinkBuilder.AccessId(Sw, 6)].Downstream.DeviceName.Should().Be("NAS (offline name)", "down, but used in the last two days");
    }

    [Fact]
    public void AccessPorts_SkipWanFabricStaleAndDevicePorts()
    {
        var ids = Build().Where(l => l.Kind == LinkKind.Access).Select(l => l.Id).ToList();
        ids.Should().NotContain(LanLinkBuilder.AccessId(Gw, 1), "fabric to the switch");
        ids.Should().NotContain(LanLinkBuilder.AccessId(Gw, 4), "down and unused for 30 days");
        ids.Should().NotContain(LanLinkBuilder.AccessId(Gw, 5), "the WAN port");
        ids.Should().NotContain(LanLinkBuilder.AccessId(Gw, 6), "a UniFi device last sat there, with no uplink block naming the port");
        ids.Should().NotContain(LanLinkBuilder.AccessId(Sw, 5), "fabric to the AP");
    }

    [Fact]
    public void SwapNeighbor_IsWhoeverIsOnTheNextPort()
    {
        var swAp = Build().Single(l => l.Id == LanLinkBuilder.WiredId(Sw, 5, Ap, 1));
        // Port 4 is up with nobody on it: the access link there is named by its port.
        swAp.SwapNeighbor.Should().Be("Port 4");
    }

    [Fact]
    public void Roles_OverrideTheModelsType()
    {
        var links = LanLinkBuilder.Build(Site(), Clients(), roles: new Dictionary<string, DeviceType> { [Sw] = DeviceType.AccessPoint });
        links.Where(l => l.Kind == LinkKind.Access).Should().NotContain(l => l.Upstream.DeviceMac == Sw, "an AP's ports are not access ports here");
    }

    [Theory]
    [InlineData("ath0", false, "Wi-Fi radios")]
    [InlineData("vwireap10", false, "Wi-Fi radios")]
    [InlineData("br0", false, "bridges")]
    [InlineData("switch0", false, "bridges")]
    [InlineData("wgsrv1", false, "VPN tunnels")]
    [InlineData("gre1", false, "GRE tunnels")]
    [InlineData("ifbeth4", false, "IFB queues")]
    [InlineData("ppp0", false, "PPPoE")]
    [InlineData("eth2.100", false, "VLAN sub-interfaces")]
    [InlineData("eth4", true, "WAN ports")]
    [InlineData("eth2", false, null)]
    public void ExcludedClass_NamesWhatLanHealthLeavesOut(string ifName, bool wan, string? cls) =>
        LanHealthPorts.ExcludedClass(ifName, wan).Should().Be(cls);

    [Fact]
    public void TheConduit_HasNoPortTableEntry_SoNeverScores() =>
        LanHealthPorts.IsScored(null, "eth6", isGateway: true).Should().BeFalse("a gateway's 10G conduit to its switch chip is not a port_table port");

    [Fact]
    public void Exclusions_ApplyByPortProfileDeviceOrAllAccess()
    {
        var access = Build().First(l => l.Id == LanLinkBuilder.AccessId(Gw, 3));
        NetworkOptimizer.Storage.Models.LanHealthExclusion X(string scope, string key) => new() { Scope = scope, Key = key, Level = 1 };
        LanHealthReportBuilder.Applies(access, X("port", access.Id)).Should().BeTrue();
        LanHealthReportBuilder.Applies(access, X("device", Gw)).Should().BeTrue("the upstream device");
        LanHealthReportBuilder.Applies(access, X("device", Pc)).Should().BeTrue("the client on it");
        LanHealthReportBuilder.Applies(access, X("access-all", "")).Should().BeTrue();
        LanHealthReportBuilder.Applies(access, X("port", "other")).Should().BeFalse();
        LanHealthReportBuilder.Applies(access, X("profile", "p1")).Should().BeFalse("the port has no profile");
    }

    [Fact]
    public void SfpReadings_TakeTheEndReceivingLessLight_WithItsWeekBest()
    {
        var devices = Site();
        devices[0].PortTable!.Add(new SwitchPort { PortIdx = 9, SfpFound = true, SfpRxPower = -7.5, SfpTemperature = 61 });
        devices[1].PortTable!.Add(new SwitchPort { PortIdx = 25, SfpFound = true, SfpRxPower = -11.2, SfpTemperature = 48 });
        var fiber = new LinkRecord
        {
            Id = "fiber", Kind = LinkKind.FabricWired,
            Upstream = new LinkEnd { DeviceMac = Gw, PortIdx = 9 }, Downstream = new LinkEnd { DeviceMac = Sw, PortIdx = 25 },
        };
        var best = new Dictionary<(string, string), double> { [(Sw, "25")] = -6.9 };
        var r = LanHealthReportBuilder.SfpReadings([fiber, Build()[0]], devices, best);
        r.Should().ContainSingle().Which.Value.Should().Be(new LanHealthReportBuilder.SfpReading(-11.2, -6.9, 48));
    }

    [Fact]
    public void UsualSpeed_OnAnAccessPort_StartsOverForANewClient()
    {
        var port = Build().First(l => l.Id == LanLinkBuilder.AccessId(Gw, 3));
        var old = new NetworkOptimizer.Storage.Models.LanHealthLinkHour { Name = "Gateway A Port 3 to Old PC", SpeedMaxMbps = 2500 };
        var now = new NetworkOptimizer.Storage.Models.LanHealthLinkHour { Name = port.Name, SpeedMaxMbps = 1000 };
        LanHealthReportBuilder.UsualSpeed(port, [old, now]).Should().Be(1000, "the 2.5G belonged to the previous client");
        var fabric = Build().First(l => l.Kind == LinkKind.FabricWired);
        LanHealthReportBuilder.UsualSpeed(fabric, [old, now]).Should().Be(2500, "a fabric link's id already changes with its device");
    }
}