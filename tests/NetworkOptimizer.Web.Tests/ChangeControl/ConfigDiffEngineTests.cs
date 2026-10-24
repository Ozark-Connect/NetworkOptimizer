using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ConfigDiffEngineTests
{
    internal static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    internal static ConfigState State(params (string Collection, string Json)[] objects)
    {
        var state = new ConfigState();
        foreach (var def in ConfigCollections.All) state.Collections[def.Key] = [];
        foreach (var (c, j) in objects) state.Collections[c].Add(Obj(j));
        return state;
    }

    [Fact]
    public void Canonical_SortsKeysAtEveryDepth()
    {
        CanonicalJson.Serialize(Obj("""{"b":1,"a":{"d":2,"c":3}}"""))
            .Should().Be("""{"a":{"c":3,"d":2},"b":1}""");
    }

    [Fact]
    public void Canonical_CollectionIsOrderedById_SoTheSameContentHashesTheSame()
    {
        var a = CanonicalJson.SerializeCollection([Obj("""{"_id":"2"}"""), Obj("""{"_id":"1"}""")]);
        var b = CanonicalJson.SerializeCollection([Obj("""{"_id":"1"}"""), Obj("""{"_id":"2"}""")]);
        a.Should().Be(b);
    }

    [Fact]
    public void UnchangedStates_HaveNoDiff()
    {
        var s = State(("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true}"""));
        ConfigDiffEngine.Compare(s, s.Clone()).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void FieldChange_IsReportedWithOldAndNew()
    {
        var before = State(("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true,"mdns_enabled":false}"""));
        var after = State(("networkconf", """{"_id":"n1","name":"IoT","vlan":30,"vlan_enabled":true,"mdns_enabled":true}"""));
        var diff = ConfigDiffEngine.Compare(before, after);
        var obj = diff.Objects.Should().ContainSingle().Subject;
        obj.Action.Should().Be(ConfigChangeAction.Updated);
        obj.Fields.Should().ContainSingle(f => f.Path == "mdns_enabled");
        obj.Tags.Should().Contain(ServiceTags.Mdns);
    }

    [Fact]
    public void SecretField_ComparesByHash_AndCarriesNoValue()
    {
        var before = State(("wlanconf", """{"_id":"w1","name":"Home","x_passphrase":"old-secret"}"""));
        var after = State(("wlanconf", """{"_id":"w1","name":"Home","x_passphrase":"new-secret"}"""));
        var field = ConfigDiffEngine.Compare(before, after).Objects.Single().Fields.Single();
        field.Secret.Should().BeTrue();
        field.Old.Should().BeNull();
        field.New.Should().BeNull();
    }

    [Fact]
    public void SetArray_OrderChangeIsNotAChange()
    {
        var before = State(("firewall_zone", """{"_id":"z1","name":"IoT","network_ids":["a","b"]}"""));
        var after = State(("firewall_zone", """{"_id":"z1","name":"IoT","network_ids":["b","a"]}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void KeyedArray_ComparesPortsByIndex()
    {
        var before = State(("device", """{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"Core","port_overrides":[{"port_idx":1,"name":"a"},{"port_idx":4,"name":"x"}]}"""));
        var after = State(("device", """{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"Core","port_overrides":[{"port_idx":4,"name":"y"},{"port_idx":1,"name":"a"}]}"""));
        var field = ConfigDiffEngine.Compare(before, after).Objects.Single().Fields.Single();
        field.Path.Should().Be("port_overrides[port_idx=4].name");
    }

    [Fact]
    public void ChurnFields_AreIgnored()
    {
        var before = State(("firewall_policy", """{"_id":"p1","name":"Block","hits":1,"last_hit":5}"""));
        var after = State(("firewall_policy", """{"_id":"p1","name":"Block","hits":99,"last_hit":9}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ClientWithoutConfig_IsTheSameAsAbsent()
    {
        var before = State();
        var after = State(("user", """{"_id":"u1","mac":"aa:bb:cc:dd:ee:ff","noted":false}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void NoOwnedGroupMembers_AreIgnored_ButTheShellIsCompared()
    {
        var before = State(("firewallgroup", """{"_id":"g1","name":"Net. Opt. Blocklist (IPv4)","group_members":["192.0.2.1"]}"""));
        var after = State(("firewallgroup", """{"_id":"g1","name":"Net. Opt. Blocklist (IPv4)","group_members":["192.0.2.2"]}"""));
        ConfigDiffEngine.Compare(before, after).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void CollectionUnreadableOnOneSide_IsSkipped_NotReportedAsDeleted()
    {
        var before = State(("portconf", """{"_id":"p1","name":"Trunk"}"""));
        var after = State();
        after.Collections.Remove("portconf");
        var diff = ConfigDiffEngine.Compare(before, after);
        diff.IsEmpty.Should().BeTrue();
        diff.SkippedCollections.Should().Contain("portconf");
    }

    [Fact]
    public void Matcher_CrossSite_MatchesNetworksByVlan_NotById()
    {
        var def = ConfigCollections.Get("networkconf")!;
        var ctx = new ConfigIdentityContext(new Dictionary<string, string>());
        var left = new List<JsonObject> { Obj("""{"_id":"a","name":"IoT","vlan":30,"vlan_enabled":true}""") };
        var right = new List<JsonObject> { Obj("""{"_id":"b","name":"Things","vlan":30,"vlan_enabled":true}""") };
        var result = ObjectMatcher.Match(def, left, right, ctx, ctx, matchById: false);
        result.Pairs.Should().ContainSingle(p => p.Left != null && p.Right != null && p.MatchedBy == "key");
    }

    [Fact]
    public void Matcher_AmbiguousName_IsAConflict()
    {
        var def = ConfigCollections.Get("portconf")!;
        var ctx = new ConfigIdentityContext(new Dictionary<string, string>());
        var left = new List<JsonObject> { Obj("""{"_id":"a","name":"Trunk"}""") };
        var right = new List<JsonObject> { Obj("""{"_id":"b","name":"Trunk"}"""), Obj("""{"_id":"c","name":"Trunk"}""") };
        var result = ObjectMatcher.Match(def, left, right, ctx, ctx, matchById: false);
        result.Conflicts.Should().ContainSingle();
    }

    [Fact]
    public void PolicyOrder_ChangeWithinAZonePair_IsReported()
    {
        var before = State(
            ("firewall_policy", """{"_id":"p1","name":"A","index":10000,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"p2","name":"B","index":10001,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        var after = State(
            ("firewall_policy", """{"_id":"p1","name":"A","index":10001,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""),
            ("firewall_policy", """{"_id":"p2","name":"B","index":10000,"source":{"zone_id":"z1"},"destination":{"zone_id":"z2"}}"""));
        ConfigDiffEngine.Compare(before, after).PolicyOrder.Should().ContainSingle();
    }

    [Fact]
    public void Reader_Device_KeepsConfigOnly_AndBuildsMeta()
    {
        var def = ConfigCollections.Get("device")!;
        var raw = Obj("""
            {"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"AP","model":"U7PIW","rx_bytes":123,"x_authkey":"k",
             "radio_table":[{"radio":"na","channel":36,"ht":80,"has_dfs":true,"max_txpower":26}],
             "port_table":[{"port_idx":1,"media":"GE","rx_bytes":5}]}
            """);
        var shaped = ConfigSnapshotReader.Shape(def, raw);
        shaped.ContainsKey("rx_bytes").Should().BeFalse();
        shaped.ContainsKey("x_authkey").Should().BeFalse();
        shaped["radio_table"]![0]!.AsObject().ContainsKey("has_dfs").Should().BeFalse();
        shaped[ConfigCollections.DeviceMetaField]!["radios"]![0]!["has_dfs"]!.GetValue<bool>().Should().BeTrue();
        shaped[ConfigCollections.DeviceMetaField]!["ports"]![0]!.AsObject().ContainsKey("rx_bytes").Should().BeFalse();
    }

    [Fact]
    public void Reader_ExtractsV1EnvelopeAndV2Array()
    {
        ConfigSnapshotReader.ExtractList("""{"meta":{"rc":"ok"},"data":[{"_id":"1"}]}""")!.Should().HaveCount(1);
        ConfigSnapshotReader.ExtractList("""[{"_id":"1"},{"_id":"2"}]""")!.Should().HaveCount(2);
        ConfigSnapshotReader.ExtractList("""{"meta":{"rc":"error"},"data":[]}""").Should().BeNull();
    }
}
