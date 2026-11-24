using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class PortAutomationAnalyzerTests
{
    private static readonly List<NetworkInfo> Networks =
    [
        new() { Id = "n-mgmt", Name = "Management", VlanId = 1, Purpose = NetworkPurpose.Management },
        new() { Id = "n-home", Name = "Home", VlanId = 10, Purpose = NetworkPurpose.Home },
        new() { Id = "n-iot", Name = "IoT", VlanId = 30, Purpose = NetworkPurpose.IoT },
    ];

    private static readonly List<UniFiPortProfile> Profiles =
    [
        new() { Id = "pc-mgmt", Name = "Mgmt Port", NativeNetworkId = "n-mgmt" },
        new() { Id = "pc-home", Name = "Home Port", NativeNetworkId = "n-home" },
        new() { Id = "pc-iot", Name = "IoT Port", NativeNetworkId = "n-iot" },
    ];

    // The 11.1.58 web UI's body: vendor_ids are numbers, mac_ouis strings.
    private static List<UniFiPortAutomation> Automations(string json) => JsonSerializer.Deserialize<List<UniFiPortAutomation>>(json)!;

    private static string One(string profile, string match = """{"vendor_ids":[42],"mac_ouis":[]}""", bool enabled = true) =>
        "[{\"id\":\"pa-1\",\"name\":\"Cameras\",\"enabled\":" + (enabled ? "true" : "false") + ",\"port_profile_id\":\"" + profile
        + "\",\"match\":" + match + ",\"scope\":{\"switch_macs\":[],\"device_tag_ids\":[]}}]";

    [Fact]
    public void ByVendorOntoManagement_IsAnIssue()
    {
        var issue = PortAutomationAnalyzer.Analyze(Automations(One("pc-mgmt")), Profiles, Networks).Should().ContainSingle().Subject;

        issue.Type.Should().Be(IssueTypes.PortAutomationTrustedNetwork);
        issue.ScoreImpact.Should().Be(8);
        issue.Message.Should().Be("Port Automation Cameras puts any device that matches its vendor on Management (Port Profile Mgmt Port). A device can claim a vendor it does not have.");
    }

    [Fact]
    public void ByOuiOntoHome_IsAnIssue_NamingTheOui()
    {
        var issue = PortAutomationAnalyzer.Analyze(Automations(One("pc-home", """{"vendor_ids":[],"mac_ouis":["aa:bb:cc"]}""")), Profiles, Networks)
            .Should().ContainSingle().Subject;
        issue.ScoreImpact.Should().Be(5);
        issue.Message.Should().Contain("matches its MAC OUI on Home");
    }

    [Fact]
    public void OntoARestrictedNetwork_IsFine()
    {
        PortAutomationAnalyzer.Analyze(Automations(One("pc-iot")), Profiles, Networks).Should().BeEmpty();
    }

    [Fact]
    public void Paused_IsNotFlagged()
    {
        PortAutomationAnalyzer.Analyze(Automations(One("pc-mgmt", enabled: false)), Profiles, Networks).Should().BeEmpty();
    }

    [Fact]
    public void NoAutomations_OrAnOlderApp_IsNoIssue()
    {
        PortAutomationAnalyzer.Analyze(null, Profiles, Networks).Should().BeEmpty();
        PortAutomationAnalyzer.Analyze([], Profiles, Networks).Should().BeEmpty();
    }

    [Fact]
    public void AWrongTypedMatch_DoesNotFailTheAutomation()
    {
        var list = Automations(One("pc-mgmt", "\"unexpected\""));

        list.Should().ContainSingle().Which.Match.Should().BeNull();
        list[0].PortProfileId.Should().Be("pc-mgmt");
        PortAutomationAnalyzer.Analyze(list, Profiles, Networks).Should().BeEmpty();
    }
}
