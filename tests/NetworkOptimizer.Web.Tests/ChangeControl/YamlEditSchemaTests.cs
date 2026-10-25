using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class YamlEditSchemaTests
{
    private static ConfigState Site() => State(
        ("networkconf", """{"_id":"n1","name":"Default","purpose":"corporate"}"""),
        ("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate","vlan":30}"""),
        ("networkconf", """{"_id":"n3","name":"IoT","purpose":"corporate","vlan":31}"""),
        ("apgroups", """{"_id":"g1","name":"All APs"}"""),
        ("apgroups", """{"_id":"g2","name":"Upstairs"}"""),
        ("wlanconf", """{"_id":"w1","name":"Home","enabled":true,"security":"wpapsk","ip_version":"IPV4","x_passphrase":"hunter2","networkconf_id":"n1","ap_group_ids":["g1"],"wlan_bands":["2g","5g"],"dhcpd_dns_1":"192.0.2.1","private_preshared_keys":[{"password":"pw1","networkconf_id":"n2"}],"overrides":[{"networkconf_id":"n2","mode":"a"}]}"""),
        ("wlanconf", """{"_id":"w2","name":"Guest","enabled":false,"security":"open","networkconf_id":"n2","ap_group_ids":["g2"],"wlan_bands":["6g"],"dhcpd_dns_1":"192.0.2.2"}"""));

    private static JsonObject Schema() => YamlConverter.EditSchema(Site(), [ConfigCollections.Get("wlanconf")!]);

    private static JsonObject Field(JsonObject schema, string name) =>
        schema["collections"]!["wlans"]!["fields"]!.AsArray().OfType<JsonObject>().Single(f => f["name"]!.ToString() == name);

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(v => v!.ToString()).ToList();

    [Fact]
    public void Fields_FollowTheExportOrder_NameFirst()
    {
        var names = Schema()["collections"]!["wlans"]!["fields"]!.AsArray().Select(f => f!["name"]!.ToString()).ToList();
        names[0].Should().Be("name");
        names.Should().Contain(["network", "ap_groups", "security", "enabled", "x_passphrase"]);
        names.Should().NotContain(["_id", "networkconf_id", "ap_group_ids"]);
    }

    [Fact]
    public void References_OfferEveryTarget_AsTheYamlTheConverterWrites()
    {
        var schema = Schema();
        Field(schema, "network")["ref"]!.ToString().Should().Be("networkconf");
        Field(schema, "ap_groups")["ref"]!.ToString().Should().Be("apgroups");

        var networks = schema["refs"]!["networkconf"]!.AsArray().Select(c => (c!["label"]!.ToString(), c["value"]!.ToString())).ToList();
        networks.Should().BeEquivalentTo([("Default", "Default"), ("IoT", "{vlan: 30}"), ("IoT", "{vlan: 31}")]);
        Strings(new JsonArray(schema["refs"]!["apgroups"]!.AsArray().Select(c => (JsonNode?)c!["label"]!.DeepClone()).ToArray()))
            .Should().Equal("All APs", "Upstairs");
    }

    [Fact]
    public void Enums_AreBooleansAndChoiceFields_NotAddressesOrFreeText()
    {
        var schema = Schema();
        Strings(Field(schema, "enabled")["values"]).Should().Equal("true", "false");
        Strings(Field(schema, "security")["values"]).Should().Equal("open", "wpapsk");
        Strings(Field(schema, "ip_version")["values"]).Should().Equal("IPV4");
        Strings(Field(schema, "wlan_bands")["values"]).Should().Equal("2g", "5g", "6g");
        Field(schema, "dhcpd_dns_1").ContainsKey("values").Should().BeFalse();
        Field(schema, "name").ContainsKey("values").Should().BeFalse();
    }

    [Fact]
    public void Secrets_NeverContributeValues()
    {
        var schema = Schema();
        var json = schema.ToJsonString();
        json.Should().NotContain("hunter2");
        json.Should().NotContain("pw1");
        Field(schema, "private_preshared_keys")["detail"]!.ToString().Should().Be("secret");
        Field(schema, "private_preshared_keys").ContainsKey("shape").Should().BeFalse();
    }

    [Fact]
    public void NestedListObjects_HaveTheirOwnShape_WithReferences()
    {
        var shape = Field(Schema(), "overrides")["shape"]!["fields"]!.AsArray().OfType<JsonObject>().ToList();
        shape.Select(f => f["name"]!.ToString()).Should().BeEquivalentTo("network", "mode");
        shape.Single(f => f["name"]!.ToString() == "network")["ref"]!.ToString().Should().Be("networkconf");
    }

    [Fact]
    public void Export_RedactsKeyCarryingFields_ThatAreNotXPrefixed()
    {
        var state = Site();
        state.Collections["networkconf"].Add(JsonNode.Parse("""{"_id":"n4","name":"WG","purpose":"vpn-client","wireguard_client_preshared_key":"psk-secret","wireguard_client_configuration_file":"[Interface]\nPrivateKey = wg-secret","openvpn_configuration":"<key>ovpn-secret</key>"}""")!.AsObject());
        var yaml = YamlConverter.Export(state, ["wlanconf", "networkconf"], includeSecrets: false);
        yaml.Should().NotContainAny("pw1", "psk-secret", "wg-secret", "ovpn-secret");
        yaml.Should().Contain("private_preshared_keys: <keep current>");
        yaml.Should().Contain("wireguard_client_configuration_file: <keep current>");
    }
}
