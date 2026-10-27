using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class DeviceReplacementTests
{
    private const string OldMac = "aa:bb:cc:00:00:01";
    private const string NewMac = "aa:bb:cc:00:00:02";
    private const string ApMac = "aa:bb:cc:00:00:10";

    private static JsonObject PortMeta(int idx, string media, bool poe = false, double watts = 0, bool up = false, bool uplink = false) => new()
    {
        ["port_idx"] = idx, ["media"] = media, ["port_poe"] = poe, ["poe_enable"] = watts > 0, ["poe_power"] = watts.ToString("0.00"),
        ["up"] = up, ["is_uplink"] = uplink,
    };

    private static JsonObject Switch(string mac, string model, IEnumerable<JsonObject> ports, JsonArray? overrides = null, JsonArray? lldp = null,
        JsonArray? lags = null, double used = 0, double budget = 0) => new()
    {
        ["_id"] = mac.Replace(":", ""), ["mac"] = mac, ["name"] = model == "US24P250" ? "Garage Switch" : "", ["model"] = model, ["type"] = "usw",
        ["port_overrides"] = overrides ?? new JsonArray(), ["link_aggregation_groups"] = lags ?? new JsonArray(), ["stp_priority"] = "12288",
        [ConfigCollections.DeviceMetaField] = new JsonObject
        {
            ["model"] = model, ["shortname"] = model, ["type"] = "usw", ["total_used_power"] = used, ["total_max_power"] = budget,
            ["ports"] = new JsonArray(ports.Select(p => (JsonNode?)p).ToArray()),
            ["lldp"] = lldp ?? new JsonArray(),
            ["uplink"] = new JsonObject { ["uplink_device_name"] = "Core Switch", ["uplink_remote_port"] = 9 },
        },
    };

    private static ConfigState Context()
    {
        var state = new ConfigState();
        state.Collections["networkconf"] = [new JsonObject { ["_id"] = "net-iot", ["name"] = "IoT" }, new JsonObject { ["_id"] = "net-cam", ["name"] = "Cameras" }];
        state.Collections["portconf"] = [new JsonObject { ["_id"] = "pc-trunk", ["name"] = "Trunk-Core" }];
        state.Collections["device"] = [new JsonObject { ["mac"] = ApMac, ["name"] = "Back Yard AP" }];
        state.Collections["wlanconf"] = [new JsonObject { ["_id"] = "w1", ["name"] = "Home", ["wlan_bands"] = new JsonArray("2g", "5g", "6g") }];
        return state;
    }

    private static JsonObject Override(int idx, string? net = null, string? profile = null) => new()
    {
        ["port_idx"] = idx, ["native_networkconf_id"] = net, ["portconf_id"] = profile,
    };

    [Fact]
    public void SameModel_MapsEveryPortToItself()
    {
        List<JsonObject> Ports() => Enumerable.Range(1, 8).Select(i => PortMeta(i, "GE", up: i < 3)).ToList();
        var old = Switch(OldMac, "USL8", Ports(), new JsonArray(Override(1, "net-iot"), Override(5, "net-cam")));
        var @new = Switch(NewMac, "USL8", Ports());

        var p = DeviceReplacement.Propose(old, @new, Context());

        p.SameLayout.Should().BeTrue();
        p.Ready.Should().BeTrue();
        DeviceReplacement.PortMap(p).Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 1, [5] = 5 });
    }

    [Fact]
    public void CrossModel_UplinkPoeAndLagGetSuitablePorts()
    {
        var oldPorts = Enumerable.Range(1, 24).Select(i => PortMeta(i, "GE", poe: true, watts: i == 1 ? 21 : 0, up: i is 1 or 3 or 23 or 24))
            .Append(PortMeta(25, "SFP+", up: true, uplink: true)).ToList();
        var newPorts = Enumerable.Range(1, 12).Select(i => PortMeta(i, "GE", poe: true))
            .Concat(Enumerable.Range(13, 4).Select(i => PortMeta(i, "2.5GE", poe: true)))
            .Concat([PortMeta(17, "SFP+", uplink: true), PortMeta(18, "SFP+")]).ToList();
        var overrides = new JsonArray(
            Override(25, profile: "pc-trunk"),
            Override(1, "net-cam"),
            Override(3, "net-iot"),
            Override(23, "net-iot"), Override(24, "net-iot"));
        var lags = new JsonArray(new JsonObject { ["lag_idx"] = 1, ["aggregate_members"] = new JsonArray(23, 24), ["type"] = "lag" });
        var lldp = new JsonArray(new JsonObject { ["local_port_idx"] = 1, ["chassis_id"] = ApMac });
        var old = Switch(OldMac, "US24P250", oldPorts, overrides, lldp, lags, used: 47, budget: 250);
        var @new = Switch(NewMac, "USWPRO16", newPorts, budget: 180);

        var p = DeviceReplacement.Propose(old, @new, Context());
        var map = DeviceReplacement.PortMap(p);

        p.SameLayout.Should().BeFalse();
        p.OldModel.Should().Be("US-24-250W", "models show as the product SKU, not the device code");
        map[25].Should().Be(17, "the uplink goes to the new uplink SFP+ port");
        map[1].Should().BeInRange(13, 16, "a powered port prefers the faster PoE ports");
        p.Ports.Single(r => r.OldPorts.Contains(1)).Connected.Should().Be("Back Yard AP (21 W)");
        map[23].Should().Be(map[24] - 1, "LAG members land on consecutive ports");
        p.Settings.Should().Contain(s => s.Label.StartsWith("LAG 1") && s.Status.StartsWith("Remapped"));
        p.Settings.Should().Contain(s => s.Label == "PoE: 47 W used, new budget 180 W" && s.Status == "Fits");
        p.Ready.Should().BeTrue();
    }

    [Fact]
    public void LagMembersSentAsStrings_AreRemapped()
    {
        var oldPorts = Enumerable.Range(1, 8).Select(i => PortMeta(i, "GE", up: i is 7 or 8)).ToList();
        var newPorts = Enumerable.Range(1, 16).Select(i => PortMeta(i, "GE")).ToList();
        var lags = new JsonArray(new JsonObject { ["lag_idx"] = 1, ["aggregate_members"] = new JsonArray("7", "8"), ["type"] = "lag" });
        var old = Switch(OldMac, "USL8", oldPorts, new JsonArray(Override(7, "net-iot"), Override(8, "net-iot")), lags: lags);
        var @new = Switch(NewMac, "USL16", newPorts);

        var p = DeviceReplacement.Propose(old, @new, Context());
        var map = DeviceReplacement.PortMap(p);

        map[7].Should().Be(map[8] - 1, "LAG members land on consecutive ports");
        p.Settings.Should().Contain(s => s.Label.StartsWith("LAG 1") && s.Status.StartsWith("Remapped"));
    }

    [Fact]
    public void ShortSwitch_RunKeepsInUsePortsAndNeedsAcknowledgement()
    {
        var oldPorts = Enumerable.Range(1, 10).Select(i => PortMeta(i, "GE", up: i is 2 or 4)).ToList();
        var newPorts = Enumerable.Range(1, 3).Select(i => PortMeta(i, "GE")).ToList();
        var overrides = new JsonArray(Enumerable.Range(1, 10).Select(i => (JsonNode?)Override(i, "net-iot")).ToArray());
        var p = DeviceReplacement.Propose(Switch(OldMac, "USW10", oldPorts, overrides), Switch(NewMac, "USW3", newPorts), Context());

        var row = p.Ports.Should().ContainSingle().Subject;
        row.OldLabel.Should().Be("Ports 1-10");
        row.Connected.Should().Be("2 in use");
        row.Target.Should().HaveCount(3);
        row.NeedsAck.Should().BeTrue();
        p.Ready.Should().BeFalse();
        DeviceReplacement.PortMap(p).Keys.Should().Contain([2, 4], "in-use ports keep a target");

        var acked = DeviceReplacement.Propose(Switch(OldMac, "USW10", oldPorts.Select(x => (JsonObject)x.DeepClone()), (JsonArray)overrides.DeepClone()),
            Switch(NewMac, "USW3", newPorts.Select(x => (JsonObject)x.DeepClone())), Context(),
            acks: new HashSet<string> { row.Key });
        acked.Ready.Should().BeTrue();
    }

    [Fact]
    public void UserChoice_DropAndExplicitTargetAreHonored()
    {
        var oldPorts = Enumerable.Range(1, 4).Select(i => PortMeta(i, "GE", up: i == 1)).ToList();
        var newPorts = Enumerable.Range(1, 4).Select(i => PortMeta(i, "GE")).ToList();
        var overrides = new JsonArray(Override(1, "net-iot"), Override(3, "net-cam"));
        var old = Switch(OldMac, "A", oldPorts, overrides);
        var @new = Switch(NewMac, "B", newPorts);
        var keys = DeviceReplacement.Propose(old, @new, Context()).Ports.ToDictionary(r => r.OldPorts[0], r => r.Key);

        var p = DeviceReplacement.Propose(old, @new, Context(), new Dictionary<string, string> { [keys[1]] = "4", [keys[3]] = DeviceReplacement.Drop },
            new HashSet<string> { keys[3] });

        DeviceReplacement.PortMap(p).Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 4 });
        p.Ready.Should().BeTrue();
        var adapted = DeviceReplacement.Adapt(p, old, @new);
        adapted["port_overrides"]!.AsArray().Should().ContainSingle().Which!["port_idx"]!.GetValue<int>().Should().Be(4);
        adapted.ContainsKey(ConfigCollections.DeviceMetaField).Should().BeFalse();
        adapted["mac"]!.GetValue<string>().Should().Be(OldMac, "the plan's device map renames it");
    }

    [Fact]
    public void Ap_ClampsWidthAndPowerAndListsLostBand()
    {
        JsonObject Ap(string mac, JsonArray radios, JsonArray caps) => new()
        {
            ["mac"] = mac, ["name"] = "Hall AP", ["type"] = "uap", ["model"] = "X", ["radio_table"] = radios,
            [ConfigCollections.DeviceMetaField] = new JsonObject { ["type"] = "uap", ["radios"] = caps },
        };
        var old = Ap(OldMac,
            new JsonArray(
                new JsonObject { ["radio"] = "na", ["name"] = "wifi1", ["ht"] = 160, ["channel"] = 100, ["tx_power_mode"] = "custom", ["tx_power"] = 29 },
                new JsonObject { ["radio"] = "6e", ["name"] = "wifi2", ["ht"] = 320 }),
            new JsonArray());
        var @new = Ap(NewMac, new JsonArray(),
            new JsonArray(new JsonObject { ["radio"] = "na", ["name"] = "wifi1", ["has_dfs"] = false, ["min_txpower"] = 6, ["max_txpower"] = 23 }));

        var p = DeviceReplacement.Propose(old, @new, Context());

        p.Kind.Should().Be("ap");
        p.Radios.Should().Contain(r => r.Band == "5 GHz" && r.Note.Contains("Width 160 to 80") && r.Note.Contains("DFS") && r.Note.Contains("Power 29 to 23"));
        p.Radios.Should().Contain(r => r.Band == "6 GHz" && r.Note.Contains("Home"));
        var radios = DeviceReplacement.Adapt(p, old, @new)["radio_table"]!.AsArray();
        radios.Should().ContainSingle();
        radios[0]!["ht"]!.GetValue<int>().Should().Be(80);
        radios[0]!["tx_power"]!.GetValue<int>().Should().Be(23);
    }
}
