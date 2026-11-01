using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>UniFi Network refuses whitespace in a custom DoH server name, with only a generic error.</summary>
public class DohServerNameTests
{
    private static ConfigState Site() => new()
    {
        Collections =
        {
            ["setting"] =
            [
                (JsonObject)JsonNode.Parse("""
                    {"_id":"s1","key":"doh","state":"custom","server_names":[],
                     "custom_servers":[{"enabled":false,"server_name":"NextDNS-a1","sdns_stamp":"sdns://AgE"},
                                       {"enabled":true,"server_name":"NextDNS-b2","sdns_stamp":"sdns://AgE"}]}
                    """)!,
            ],
        },
    };

    private static List<YamlProblem> Edit(string from, string to)
    {
        var state = Site();
        var yaml = YamlConverter.Export(state, ["setting"], SecretMode.Mask, ids: true).Replace(from, to);
        return YamlConverter.Parse(yaml, state, "settings.yaml").Problems;
    }

    [Fact]
    public void NameWithASpace_IsAnError()
    {
        Edit("NextDNS-a1", "NextDNS-a1 2").Should().ContainSingle(p => p.Level == "Error")
            .Which.Message.Should().Contain("\"NextDNS-a1 2\" contains a space");
    }

    [Fact]
    public void NameWithoutSpaces_IsFine()
    {
        Edit("NextDNS-a1", "NextDNS-a1-2").Should().BeEmpty();
    }

    [Fact]
    public void Helper_FlagsAnyWhitespace_AndOnlyThoseNames()
    {
        var doh = (JsonObject)JsonNode.Parse("""{"custom_servers":[{"server_name":"ok"},{"server_name":"tab\there"},{"server_name":"a b"}]}""")!;
        PlanBuilder.DohServerNameProblems(doh).Should().HaveCount(2);
    }
}
