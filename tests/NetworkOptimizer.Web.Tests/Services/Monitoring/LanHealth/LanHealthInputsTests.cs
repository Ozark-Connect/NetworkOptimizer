using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LanHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.Monitoring.LanHealth;

/// <summary>
/// LAN Health's inputs and storage: speed test attribution and norms, Port Insights, the stored incident JSON,
/// and the tolerant converters on the new UniFi fields.
/// </summary>
public class LanHealthInputsTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private const string Gw = "aa:bb:cc:00:00:01", Sw = "aa:bb:cc:00:00:02", Ap = "aa:bb:cc:00:00:03";

    private static LinkRecord Wired(string id, string up, string down, int speed = 2500, DeviceType upType = DeviceType.Gateway, string downName = "Switch A") => new()
    {
        Id = id, Kind = LinkKind.FabricWired, SpeedMbps = speed,
        Upstream = new LinkEnd { DeviceMac = up, DeviceName = "Gateway A", DeviceType = upType, PortIdx = 1 },
        Downstream = new LinkEnd { DeviceMac = down, DeviceName = downName, DeviceType = DeviceType.Switch },
    };

    private static readonly List<LinkRecord> Links = [Wired("gw-sw", Gw, Sw), Wired("sw-ap", Sw, Ap, upType: DeviceType.Switch, downName: "AP 1")];

    private static LanHealthSpeedTests.Test Test(DateTime at, double down, double up = 100, string shape = "s1", bool routed = false, params string[] hops) =>
        new(at, down, up, 0, shape, "ServerToDevice", routed, hops.Length > 0 ? hops : [Gw, Sw, Ap]);

    // ---- Speed tests ----

    [Fact]
    public void FromResults_KeepsSuccessfulTestsWithAPath_InHopOrder()
    {
        var path = new PathAnalysisResult { Path = new NetworkPath { TheoreticalMaxMbps = 2500, RequiresRouting = false, Hops = [new() { Order = 2, DeviceMac = "AA-BB-CC-00-00-03" }, new() { Order = 1, DeviceMac = Sw }] } };
        var ok = new Iperf3Result { Success = true, TestTime = Now, DownloadBitsPerSecond = 900e6, UploadBitsPerSecond = 800e6, Direction = SpeedTestDirection.ServerToDevice, ParallelStreams = 4, PathAnalysis = path };
        var failed = new Iperf3Result { Success = false, TestTime = Now, PathAnalysis = path };
        var noPath = new Iperf3Result { Success = true, TestTime = Now };
        var t = LanHealthSpeedTests.FromResults([ok, failed, noPath]).Single();
        t.HopMacs.Should().Equal(Sw, Ap);
        t.DownMbps.Should().Be(900);
        t.Shape.Should().Be("2500|2|ServerToDevice|4|False");
    }

    [Fact]
    public void Norms_NeedThreeTestsOfAShape()
    {
        var norms = LanHealthSpeedTests.Norms([Test(Now, 900), Test(Now, 940), Test(Now, 920), Test(Now, 100, shape: "s2"), Test(Now, 110, shape: "s2")]);
        norms.Should().ContainKey("s1").WhoseValue.Down.Should().Be(920);
        norms.Should().NotContainKey("s2");
    }

    [Fact]
    public void Attribute_PutsATestOnEveryLinkItCrossed_InTheWindow()
    {
        var tests = new[] { Test(Now.AddDays(-3), 900), Test(Now.AddDays(-2), 920), Test(Now.AddDays(-1), 940), Test(Now.AddHours(-1), 400), Test(Now.AddHours(-2), 300, hops: [Gw, Sw]) };
        var byLink = LanHealthSpeedTests.Attribute(tests, Links, Now.AddHours(-48), Now);
        byLink["gw-sw"].Should().HaveCount(4, "three whole-path tests from the window's start on, and the one to the switch; the 3-day-old one is out");
        byLink["sw-ap"].Should().HaveCount(3).And.OnlyContain(t => t.NormDownMbps == 900, "the median of every whole-path test");
    }

    [Fact]
    public void GatewayForwarding_RoutedTestsAgainstNormsFromBeforeTheWindow()
    {
        var tests = new List<LanHealthSpeedTests.Test>
        {
            Test(Now.AddDays(-5), 900, routed: true), Test(Now.AddDays(-4), 900, routed: true), Test(Now.AddDays(-3), 900, routed: true),
            Test(Now.AddHours(-2), 360, up: 0, routed: true), Test(Now.AddHours(-1), 450, up: 0, routed: true), Test(Now.AddHours(-1), 100, up: 0),
        };
        var g = LanHealthSpeedTests.GatewayForwarding(tests, Now.AddHours(-48), Now)!.Value;
        g.Tests.Should().Be(2, "the unrouted test is not the gateway's");
        g.RatioToNorm.Should().BeApproximately(0.45, 1e-9);
        LanHealthSpeedTests.GatewayForwarding(tests.Take(3).ToList(), Now.AddHours(-48), Now).Should().BeNull();
    }

    [Fact]
    public void Suggestions_ForEachUntestedFabricLink_TheCheapestTest()
    {
        var s = LanHealthSpeedTests.Suggestions([Test(Now.AddDays(-2), 900, hops: [Gw, Sw])], Links, Now, mac => mac == Ap).ToList();
        s.Should().ContainSingle().Which.Body.Should().Be("No speed test has run across this link in 30 days. Run a LAN Speed Test to AP 1 to measure it.");
        LanHealthSpeedTests.Suggestions([], [Links[0]], Now, _ => false).Single().Body
            .Should().Be("No speed test has run across this link in 30 days. Run a Client Speed Test from a wired client connected below Switch A to measure it.");
    }

    [Fact]
    public void CrossVlanCandidate_AFastWiredClientOnAnotherNetwork()
    {
        var clients = new List<(string, string, string?, int?, string?)>
        {
            ("aa:bb:cc:00:01:01", "Workstation", "Office", 2500, Sw),
            ("aa:bb:cc:00:01:02", "Printer", "Office", 100, Sw),
            ("aa:bb:cc:00:01:03", "NAS", "Default", 10000, Sw),
        };
        var f = LanHealthSpeedTests.CrossVlanCandidate([], Links, clients, "Default", Now)!;
        f.Title.Should().Be("Cross-VLAN Test: Workstation");
        LanHealthSpeedTests.CrossVlanCandidate([Test(Now.AddDays(-1), 900, routed: true)], Links, clients, "Default", Now).Should().BeNull("a routed test already ran");
    }

    // ---- Port Insights ----

    [Fact]
    public void PortInsights_ReadEitherShapeAndEitherClock()
    {
        var arr = JsonDocument.Parse("""[{"device_mac":"AA:BB:CC:00:00:02","port_idx":5,"timestamp":1763900000,"anomaly":"link_flap"},{"mac":"x"}]""").RootElement;
        var a = LanHealthPortInsights.Parse(arr).Single();
        a.DeviceMac.Should().Be(Sw);
        a.PortIdx.Should().Be(5);
        a.At.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1763900000).UtcDateTime);
        a.Kind.Should().Be("link_flap");
        var wrapped = JsonDocument.Parse("""{"data":[{"mac":"aa:bb:cc:00:00:02","port":3,"time":1763900000000}]}""").RootElement;
        LanHealthPortInsights.Parse(wrapped).Single().At.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1763900000000).UtcDateTime);
        LanHealthPortInsights.Parse(JsonDocument.Parse("""{"meta":{}}""").RootElement).Should().BeEmpty();
    }

    // ---- Stored incidents ----

    [Fact]
    public void IncidentJson_RoundTripsWhatTheDetectorsRead()
    {
        var i = new LinkIncident { Start = Now, End = Now.AddSeconds(4), Kind = IncidentKind.Drop, Count = 1, Explained = ExplainReason.BackhaulChannelChange, ExplainedDetail = "116>36", SpeedAfterMbps = 2500, AttachedErrors = 3 };
        i.SeenBy.Add(LinkSource.ApKernelLog);
        i.MissedBy.Add(LinkSource.SwitchLinkDownCount);
        i.Events.Add(new LinkEvent(Now, Now, LinkEventType.LinkDown, LinkSource.ApKernelLog, LinkSide.Downstream));
        i.Events.Add(new LinkEvent(Now.AddSeconds(4), Now.AddSeconds(4), LinkEventType.LinkUp, LinkSource.ApKernelLog, LinkSide.Downstream));
        var back = LanHealthIncidentJson.Deserialize(LanHealthIncidentJson.Serialize([i])).Single();
        back.Should().BeEquivalentTo(i, o => o.Excluding(x => x.Events));
        back.DownSeconds.Should().Be(i.DownSeconds);
        back.Start.Kind.Should().Be(DateTimeKind.Utc);
        LanHealthIncidentJson.Deserialize("{broken").Should().BeEmpty();
    }

    // ---- Tolerant converters ----

    private sealed class Counters
    {
        [JsonPropertyName("n")]
        [JsonConverter(typeof(FlexibleNullableLongConverter))]
        public long? N { get; set; }

        [JsonPropertyName("items")]
        [JsonConverter(typeof(FlexibleObjectListConverter<Item>))]
        public List<Item>? Items { get; set; }
    }

    private sealed class Item
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    [Theory]
    [InlineData("""{"n":12}""", 12L)]
    [InlineData("""{"n":"12"}""", 12L)]
    [InlineData("""{"n":12.9}""", 12L)]
    [InlineData("""{"n":""}""", null)]
    [InlineData("""{"n":true}""", null)]
    [InlineData("""{"n":null}""", null)]
    [InlineData("""{}""", null)]
    public void FlexibleNullableLong_KeepsNotReportedApartFromZero(string json, long? expected) =>
        JsonSerializer.Deserialize<Counters>(json)!.N.Should().Be(expected);

    [Fact]
    public void FlexibleObjectList_SkipsABadItemOrANonArray()
    {
        JsonSerializer.Deserialize<Counters>("""{"items":[{"id":1},{"id":"x"},{"id":3}]}""")!.Items!.Select(i => i.Id).Should().Equal(1, 3);
        JsonSerializer.Deserialize<Counters>("""{"items":{"id":1},"n":5}""")!.Should().Match<Counters>(c => c.Items == null && c.N == 5);
    }

    [Theory]
    [InlineData("""{"port_idx":3,"last_connection":{"mac":"aa:bb:cc:00:00:01","last_seen":1763900000}}""", "aa:bb:cc:00:00:01")]
    [InlineData("""{"port_idx":3,"last_connection":"none"}""", null)]
    [InlineData("""{"port_idx":3,"last_connection":[]}""", null)]
    public void LastConnection_AnOddShapeNeverFailsThePort(string json, string? mac)
    {
        var port = JsonSerializer.Deserialize<SwitchPort>(json)!;
        port.PortIdx.Should().Be(3);
        port.LastConnection?.Mac.Should().Be(mac);
        if (mac == null) port.LastConnection.Should().BeNull();
    }
}