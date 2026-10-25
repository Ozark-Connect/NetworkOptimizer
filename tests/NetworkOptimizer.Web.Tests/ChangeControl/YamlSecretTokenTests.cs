using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class YamlSecretTokenTests
{
    private static ConfigState Site() => State(
        ("networkconf", """{"_id":"n1","name":"LAN","purpose":"corporate"}"""),
        ("networkconf", """{"_id":"n2","name":"IoT","purpose":"corporate"}"""),
        ("wlanconf", """{"_id":"w1","name":"Home","x_passphrase":"home-pass","networkconf_id":"n1","private_preshared_keys":[{"networkconf_id":"n1","password":"pw-lan"},{"networkconf_id":"n2","password":"pw-iot"}]}"""),
        ("wlanconf", """{"_id":"w2","name":"Guest","x_passphrase":"guest-pass","networkconf_id":"n2"}"""));

    private static YamlParseResult Parse(string yaml) => YamlConverter.Parse(yaml, Site(), "wlans.yaml");

    [Fact]
    public void Mask_WritesTokens_NeverValues()
    {
        var yaml = YamlConverter.Export(Site(), ["wlanconf"], SecretMode.Mask);
        yaml.Should().NotContainAny("home-pass", "guest-pass", "pw-lan", "pw-iot");
        yaml.Should().Contain("x_passphrase: <secret:w1/x_passphrase>");
        yaml.Should().Contain("password: <secret:w1/private_preshared_keys/1/password>");
    }

    [Fact]
    public void Tokens_RoundTrip_ToTheExactValues()
    {
        var parsed = Parse(YamlConverter.Export(Site(), ["wlanconf"], SecretMode.Mask));
        parsed.Problems.Should().NotContain(p => p.Level == "Error");
        var home = parsed.Collections["wlanconf"].Single(o => o["name"]!.ToString() == "Home");
        home["x_passphrase"]!.ToString().Should().Be("home-pass");
        home["private_preshared_keys"]!.AsArray().Select(k => k!["password"]!.ToString()).Should().Equal("pw-lan", "pw-iot");
    }

    [Fact]
    public void MovedEntry_KeepsItsOwnPassword()
    {
        var yaml = """
            wlanconf:
              - name: Home
                x_passphrase: <secret:w1/x_passphrase>
                network: LAN
                private_preshared_keys:
                  - network: IoT
                    password: <secret:w1/private_preshared_keys/1/password>
                  - network: LAN
                    password: <secret:w1/private_preshared_keys/0/password>
            """;
        var parsed = Parse(yaml);
        parsed.Problems.Should().NotContain(p => p.Level == "Error");
        parsed.Collections["wlanconf"].Single()["private_preshared_keys"]!.AsArray()
            .Select(k => k!["password"]!.ToString()).Should().Equal("pw-iot", "pw-lan");
    }

    [Fact]
    public void Token_CanReuseAnotherObjectsSecret_InASecretField()
    {
        var parsed = Parse("wlanconf:\n  - name: Guest\n    network: IoT\n    x_passphrase: <secret:w1/x_passphrase>\n");
        parsed.Problems.Should().NotContain(p => p.Level == "Error");
        parsed.Collections["wlanconf"].Single()["x_passphrase"]!.ToString().Should().Be("home-pass");
    }

    [Fact]
    public void Token_InAnOrdinaryField_IsRefused_AndNotResolved()
    {
        var parsed = Parse("wlanconf:\n  - name: <secret:w1/x_passphrase>\n    network: IoT\n");
        parsed.Problems.Should().Contain(p => p.Level == "Error" && p.Message.Contains("secret field"));
        parsed.Collections["wlanconf"].Single()["name"]!.ToString().Should().NotBe("home-pass");
    }

    [Fact]
    public void Token_PointingAtAnOrdinaryField_IsRefused()
    {
        YamlConverter.FindSecret(Site(), "w1/name").Should().BeNull("a token can only read out a secret field");
        var parsed = Parse("wlanconf:\n  - name: Guest\n    network: IoT\n    x_passphrase: <secret:w1/name>\n");
        parsed.Problems.Should().Contain(p => p.Level == "Error" && p.Message.Contains("not in the config"));
    }

    [Fact]
    public void ScalarText_QuotesValuesThatNeedIt()
    {
        YamlConverter.ScalarText(System.Text.Json.Nodes.JsonValue.Create("a: b #c")).Should().Be("\"a: b #c\"");
        YamlConverter.ScalarText(System.Text.Json.Nodes.JsonValue.Create("tumble-cactus")).Should().Be("tumble-cactus");
    }
}
