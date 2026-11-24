using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class WanPassthroughAnalyzerTests
{
    // The shape Network 11.1.58 returns (rest/networkconf): a WAN naming a VLAN Only Network in tagged_networkconf_ids.
    private const string Json = """
        [
          {"_id":"n-lan","name":"Default","purpose":"corporate","vlan_enabled":false},
          {"_id":"n-wan","name":"Fiber","purpose":"wan","wan_networkgroup":"WAN","tagged_networkconf_ids":["n-iptv"]},
          {"_id":"n-wan2","name":"Starlink","purpose":"wan","wan_networkgroup":"WAN2"},
          {"_id":"n-iptv","name":"IPTV","purpose":"vlan-only","vlan_enabled":true,"vlan":690}
        ]
        """;

    private static List<UniFiNetworkConfig> Networks(string json = Json) => JsonSerializer.Deserialize<List<UniFiNetworkConfig>>(json)!;

    [Fact]
    public void WanWithPassthroughVlans_IsOneIssue_NamingTheVlans()
    {
        var issues = WanPassthroughAnalyzer.Analyze(Networks(), "Gateway");

        var issue = issues.Should().ContainSingle().Subject;
        issue.Type.Should().Be(IssueTypes.WanPassthroughVlan);
        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.Message.Should().Be("Fiber passes ISP traffic straight to VLAN 690 (IPTV). UniFi does not filter passthrough traffic with the firewall.");
    }

    [Theory]
    [InlineData("""[{"_id":"n-wan","name":"Fiber","purpose":"wan"}]""")]
    [InlineData("""[{"_id":"n-wan","name":"Fiber","purpose":"wan","tagged_networkconf_ids":[]}]""")]
    [InlineData("""[{"_id":"n-wan","name":"Fiber","purpose":"wan","tagged_networkconf_ids":null}]""")]
    public void NoPassthroughVlans_IsNoIssue(string json)
    {
        // Before Network 11.1 the field is absent; with none set it is empty or absent.
        WanPassthroughAnalyzer.Analyze(Networks(json)).Should().BeEmpty();
    }

    [Fact]
    public void ALanNetworksTaggedIds_AreNotPassthrough()
    {
        var json = """[{"_id":"n-lan","name":"Default","purpose":"corporate","tagged_networkconf_ids":["n-x"]},{"_id":"n-x","name":"X","purpose":"vlan-only","vlan":5}]""";
        WanPassthroughAnalyzer.Analyze(Networks(json)).Should().BeEmpty();
    }

    [Fact]
    public void NullOrEmptyInput_IsNoIssue()
    {
        WanPassthroughAnalyzer.Analyze(null).Should().BeEmpty();
        WanPassthroughAnalyzer.Analyze([]).Should().BeEmpty();
    }
}
