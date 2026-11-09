using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>How each device finds the UniFi Console after a re-address, and the Inform Host override write.</summary>
public class ReaddressInformTests
{
    private const string Console = "192.168.99.220";

    /// <summary>Default (VLAN 1) and Management (VLAN 99), a gateway, a static switch, and a DHCP AP.</summary>
    private static ConfigState Site(string? overrideHost = null, bool overrideOn = false) => State(
        ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate","ip_subnet":"192.168.1.1/24","dhcpd_enabled":true}"""),
        ("networkconf", """{"_id":"n99","name":"Management","purpose":"corporate","vlan_enabled":true,"vlan":99,"ip_subnet":"192.168.99.1/24","dhcpd_enabled":true}"""),
        ("device", """{"_id":"gw","mac":"aa:bb:cc:dd:ee:10","name":"Gateway","config_network":{"type":"dhcp"}}"""),
        ("device", """{"_id":"sw","mac":"aa:bb:cc:dd:ee:11","name":"Switch","config_network":{"type":"static","ip":"192.168.99.11","dns1":"192.168.99.1"}}"""),
        ("device", """{"_id":"ap","mac":"aa:bb:cc:dd:ee:12","name":"AP","config_network":{"type":"dhcp"}}"""),
        ("setting", $$"""{"_id":"s1","key":"super_mgmt","override_inform_host":{{(overrideOn ? "true" : "false")}},"override_inform_host_location":"{{overrideHost ?? ""}}"}"""));

    private static List<JsonObject> Stat(string informHost) =>
    [
        Row("aa:bb:cc:dd:ee:10", "Gateway", "ugw", "192.168.1.1", informHost),
        Row("aa:bb:cc:dd:ee:11", "Switch", "usw", "192.168.99.11", informHost),
        Row("aa:bb:cc:dd:ee:12", "AP", "uap", "192.168.99.31", informHost),
    ];

    private static JsonObject Row(string mac, string name, string type, string ip, string informHost) => new()
    {
        ["mac"] = mac, ["name"] = name, ["type"] = type, ["ip"] = ip, ["inform_url"] = $"http://{informHost}:8080/inform",
    };

    private static List<SubnetMove> Moves(ConfigState s) => Readdressing.Proposal(s, "10.42.{vlan}.0/24").Where(m => m.OldCidr != m.NewCidr).ToList();

    private static InformVerdicts Classify(ConfigState live, string informHost, bool standalone, Dictionary<string, uint?>? resolved = null)
    {
        var moves = Moves(live);
        return ReaddressInform.Classify(Stat(informHost), live, Readdressing.Apply(live, moves).Desired, Readdressing.Map(moves), standalone,
            resolved ?? []);
    }

    private static InformRoute RouteOf(InformVerdicts v, string name) => v.Devices.Single(d => d.Name == name).Route;

    [Fact]
    public void InformHostOutsideEveryMovedSubnet_IsNotAffected()
    {
        var v = Classify(Site(), "172.16.0.5", standalone: true);

        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.NotAffected);
        v.OverrideIp.Should().BeNull();
        ChangeControlService.InformWarnings(v).Should().BeEmpty();
    }

    [Fact]
    public void GatewayHostedConsole_FollowsAutomatically_AndGetsNoOverrideWrite()
    {
        var v = Classify(Site(), Console, standalone: false);

        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.FollowsAutomatically);
        v.OverrideIp.Should().BeNull();
    }

    [Fact]
    public void InformHostIsTheGatewaysOwnLanAddress_FollowsAutomatically()
    {
        Classify(Site(), "192.168.1.1", standalone: true).Devices.Should().OnlyContain(d => d.Route == InformRoute.FollowsAutomatically);
    }

    [Fact]
    public void NameTheGatewayServesAndThePlanMoves_FollowsByDns_AndAnOverrideNameIsKept()
    {
        var live = Site("unifi.lan", overrideOn: true);
        live.Collections["static_dns"] = [JsonNode.Parse($$"""{"_id":"d1","key":"unifi.lan","record_type":"A","value":"{{Console}}"}""")!.AsObject()];

        var v = Classify(live, "unifi.lan", standalone: true);

        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.FollowsByDns);
        v.OverrideIp.Should().BeNull("an override on with a name stays the user's name");
        v.Devices.Should().OnlyContain(d => d.SetInformUrl == "http://unifi.lan:8080/inform");
        v.Devices.Should().OnlyContain(d => d.SetInformIpUrl == "http://10.42.99.220:8080/inform", "the record's moved address, for a device whose DNS is gone");
        var sw = v.Devices.Single(d => d.Name == "Switch");
        ReaddressInform.Command(sw).Should().Be("set-inform http://unifi.lan:8080/inform");
        ReaddressInform.Command(sw, atOldAddress: true).Should().Be("set-inform http://10.42.99.220:8080/inform");
        ReaddressInform.HandCommand(sw).Should().Be("ip route add 10.42.99.220/32 dev <if>; set-inform http://10.42.99.220:8080/inform");
    }

    [Fact]
    public void StandaloneConsoleInAMovedSubnet_OverrideOff_ThePlanWritesTheOverrideToTheNewIp()
    {
        var v = Classify(Site(), Console, standalone: true);

        v.OverrideIp.Should().Be("10.42.99.220");
        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.ConsoleRepointed);
        ChangeControlService.InformWarnings(v).Should().ContainSingle()
            .Which.Detail.Should().Contain("10.42.99.220").And.Contain("Re-address the Console's host in the same window");
    }

    [Fact]
    public void OverrideAlreadyOnWithAnIp_TheRewriteMovesIt_NoExtraWriteNoWarning()
    {
        var live = Site(Console, overrideOn: true);

        var v = Classify(live, Console, standalone: true);

        v.OverrideIp.Should().BeNull();
        v.ConsoleRepointed.Should().BeTrue();
        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.ConsoleRepointed);
        ChangeControlService.InformWarnings(v).Should().BeEmpty();
        var moves = Moves(live);
        Readdressing.Apply(live, moves).Desired.Get("setting")[0]["override_inform_host_location"]!.ToString().Should().Be("10.42.99.220");
    }

    [Fact]
    public void NameServedByAPihole_IsSshFallback_AndSetInformUsesTheNewIp()
    {
        var v = Classify(Site("unifi.lan", overrideOn: true), "unifi.lan", standalone: true,
            new Dictionary<string, uint?> { ["unifi.lan"] = Readdressing.ParseIp(Console) });

        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.SshFallback);
        v.Devices.Should().OnlyContain(d => d.SetInformUrl == "http://10.42.99.220:8080/inform");
        ChangeControlService.InformWarnings(v).Should().ContainSingle()
            .Which.Detail.Should().Contain("Gateway, Switch, AP").And.Contain("re-pointed over SSH if it does not reconnect");
    }

    [Fact]
    public void WithoutDeviceSsh_TheDryRunSaysTheAlertGivesTheCommand()
    {
        var v = Classify(Site("unifi.lan", overrideOn: true), "unifi.lan", standalone: true,
            new Dictionary<string, uint?> { ["unifi.lan"] = Readdressing.ParseIp(Console) }) with { DeviceSsh = false };

        ChangeControlService.InformWarnings(v).Should().ContainSingle()
            .Which.Detail.Should().Contain("Device SSH is not configured").And.NotContain("re-pointed over SSH");
    }

    [Fact]
    public void StaticDnsAtAnotherGatewayAddress_FollowsByDns()
    {
        var live = Site("unifi.lan", overrideOn: true);
        live.Collections["static_dns"] = [JsonNode.Parse($$"""{"_id":"d1","key":"unifi.lan","record_type":"A","value":"{{Console}}"}""")!.AsObject()];
        live.Get("device")[1]["config_network"]!["dns1"] = "192.168.1.1";

        RouteOf(Classify(live, "unifi.lan", standalone: true), "Switch").Should().Be(InformRoute.FollowsByDns);
    }

    [Fact]
    public void NameTheGatewayServes_ButTheNetworkHandsOutAPihole_IsSshFallback()
    {
        var live = Site("unifi.lan", overrideOn: true);
        live.Collections["static_dns"] = [JsonNode.Parse($$"""{"_id":"d1","key":"unifi.lan","record_type":"A","value":"{{Console}}"}""")!.AsObject()];
        live.Get("networkconf")[1]["dhcpd_dns_enabled"] = true;
        live.Get("networkconf")[1]["dhcpd_dns_1"] = "192.168.99.53";

        var v = Classify(live, "unifi.lan", standalone: true);

        RouteOf(v, "AP").Should().Be(InformRoute.SshFallback, "it asks the Pi-hole, which the plan does not move");
        RouteOf(v, "Switch").Should().Be(InformRoute.FollowsByDns, "its static DNS is the gateway");
        RouteOf(v, "Gateway").Should().Be(InformRoute.FollowsByDns);
    }

    [Fact]
    public void UnresolvedNameOnAStandaloneConsole_IsSshFallback()
    {
        Classify(Site("unifi.lan", overrideOn: true), "unifi.lan", standalone: true).Devices
            .Should().OnlyContain(d => d.Route == InformRoute.SshFallback);
    }

    [Fact]
    public void Verdicts_CarryTheNewStaticAndTheOldAddress()
    {
        var v = Classify(Site(), Console, standalone: true);

        var sw = v.Devices.Single(d => d.Name == "Switch");
        sw.NewIp.Should().Be("10.42.99.11");
        sw.OldIp.Should().Be("192.168.99.11");
        sw.Role.Should().Be(DeviceType.Switch);
        v.Devices.Single(d => d.Name == "AP").NewIp.Should().BeNull("DHCP: no known new address");
        v.Devices.Single(d => d.Name == "Gateway").Role.Should().Be(DeviceType.Gateway);
    }

    [Fact]
    public void HandCommand_AddsAHostRoute_OnlyWhenTheConsoleIsOnTheDevicesNetwork()
    {
        var v = Classify(Site(), Console, standalone: true);

        var sw = v.Devices.Single(d => d.Name == "Switch");
        sw.ConsoleOnLink.Should().BeTrue();
        ReaddressInform.HandCommand(sw).Should().Be("ip route add 10.42.99.220/32 dev <if>; set-inform http://10.42.99.220:8080/inform");
        var gw = v.Devices.Single(d => d.Name == "Gateway");
        gw.ConsoleOnLink.Should().BeFalse("its address is on Default, the Console on Management");
        ReaddressInform.HandCommand(gw).Should().Be("set-inform http://10.42.99.220:8080/inform");
    }

    [Theory]
    [InlineData("http://192.168.99.220:8080/inform", false, null, "http://10.42.99.220:8080/inform")]
    [InlineData("http://172.16.0.5:8080/inform", false, null, "http://172.16.0.5:8080/inform")]
    [InlineData("http://unifi.lan:8080/inform", true, "192.168.99.220", "http://unifi.lan:8080/inform")]
    [InlineData("http://unifi.lan:8080/inform", false, "192.168.99.220", "http://10.42.99.220:8080/inform")]
    [InlineData("http://unifi.lan:8080/inform", false, null, "http://unifi.lan:8080/inform")]
    [InlineData("http://unifi.example.com:8080/inform", false, "198.51.100.7", "http://unifi.example.com:8080/inform")]
    public void InformUrlAfter_MapsAnIp_KeepsAServedName_ReplacesAnUnservedOne(string url, bool served, string? resolved, string expected)
    {
        var map = Readdressing.Map(Moves(Site()));

        ReaddressInform.InformUrlAfter(url, map, served, Readdressing.ParseIp(resolved)).Should().Be(expected);
    }

    private static PlanDocument OverridePlan(ConfigState live, out List<SubnetMove> moves)
    {
        moves = Moves(live);
        var desired = Readdressing.Apply(live, moves).Desired;
        var mgmt = ReaddressInform.SuperMgmt(desired)!;
        mgmt["override_inform_host"] = true;
        mgmt["override_inform_host_location"] = "10.42.99.220";
        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });
        ChangeControlService.OrderReaddressWrites(doc, new HashSet<string> { "n99" }, new HashSet<string> { "gw" }, moves, ChangeControlService.DeviceDepths(live));
        return doc;
    }

    [Fact]
    public void OverrideWrite_GoesJustBeforeTheConsolePathNetwork_AfterEveryOtherWrite()
    {
        var doc = OverridePlan(Site(), out _);

        int At(string id) => doc.Operations.FindIndex(o => o.ObjectId == id && !o.Key.EndsWith(ChangeControlService.HoneypotsOffSuffix));
        doc.Operations[At("s1")].Order.Should().Be(ChangeControlService.InformOverrideOrder);
        At("s1").Should().BeGreaterThan(At("gw"));
        At("s1").Should().BeGreaterThan(At("sw"));
        At("s1").Should().BeGreaterThan(At("n1"));
        At("s1").Should().Be(At("n99") - 1);
        // Not a path write: the Console still answers at its old address, so a lost reply is a failure.
        doc.Context[PlanCoordinator.ReaddressPathContext].Should().NotContain(doc.Operations[At("s1")].Key);
    }

    [Fact]
    public void OverrideAlreadyOnWithAnIp_ItsRewriteAlsoGoesJustBeforeTheConsolePathNetwork()
    {
        var live = Site(Console, overrideOn: true);
        var moves = Moves(live);
        var desired = Readdressing.Apply(live, moves).Desired;
        var doc = PlanBuilder.Build(desired, live, new PlanOptions { DeleteMissing = false });

        ChangeControlService.OrderReaddressWrites(doc, new HashSet<string> { "n99" }, new HashSet<string> { "gw" }, moves, ChangeControlService.DeviceDepths(live));

        int At(string id) => doc.Operations.FindIndex(o => o.ObjectId == id);
        doc.Operations[At("s1")].WriteFields.Should().NotContain("override_inform_host", "only the address changes");
        doc.Operations[At("s1")].Order.Should().Be(ChangeControlService.InformOverrideOrder);
        At("s1").Should().BeGreaterThan(At("sw"));
        At("s1").Should().Be(At("n99") - 1);
    }

    [Fact]
    public void ConsoleAtAVpnServerNetworksGatewayAddress_StillGetsTheOverrideWrite()
    {
        var live = Site();
        live.Collections["networkconf"].Add(JsonNode.Parse(
            $$"""{"_id":"vpn","name":"VPN Server","purpose":"remote-user-vpn","ip_subnet":"{{Console}}/30"}""")!.AsObject());

        var v = Classify(live, Console, standalone: true);

        v.OverrideIp.Should().Be("10.42.99.220", "only LAN networks' addresses are the gateway's");
        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.ConsoleRepointed);
    }

    [Fact]
    public void ConsoleAtAGatewayLanAddress_NeverGetsAnOverrideWrite_EvenWhenTheTypeDidNotRead()
    {
        var v = Classify(Site(), "192.168.1.1", standalone: true);

        v.OverrideIp.Should().BeNull();
        v.Devices.Should().OnlyContain(d => d.Route == InformRoute.FollowsAutomatically);
        v.ConsoleFollows.Should().BeTrue("the wording follows the same Gateway-address test as the override");
    }

    [Fact]
    public void RollBack_PutsTheOverrideBackJustBeforeTheConsolePathNetwork()
    {
        var live = Site();
        var forward = OverridePlan(live, out var moves);
        forward.Context[PlanCoordinator.ReaddressContext] = System.Text.Json.JsonSerializer.Serialize(moves);
        var after = PlanBuilder.PlannedState(live, forward);

        var revert = PlanBuilder.Build(live, after, new PlanOptions { DeleteMissing = false });
        PlanCoordinator.ReaddressRevertOrder(forward, live)!(revert);

        int At(string id) => revert.Operations.FindIndex(o => o.ObjectId == id && !o.Key.EndsWith(ChangeControlService.HoneypotsOffSuffix));
        revert.Operations[At("s1")].Body!["override_inform_host"]!.GetValue<bool>().Should().BeFalse();
        At("s1").Should().Be(At("n99") - 1);
    }
}
