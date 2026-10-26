using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;
using static NetworkOptimizer.Web.Tests.ChangeControl.ConfigDiffEngineTests;
using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class ImpactAnalyzerTests
{
    [Fact]
    public void Overlay_ReplacesConfig_KeepsTelemetry()
    {
        var state = State(("device", """
            {"_id":"d1","mac":"AA:BB:CC:DD:EE:01","name":"Old name","port_overrides":[{"port_idx":1,"native_networkconf_id":"n2"}],
             "radio_table":[{"radio":"na","channel":149}]}
            """));
        var live = """
            {"meta":{"rc":"ok"},"data":[{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"New name","rx_bytes":42,
             "port_overrides":[],"radio_table":[{"radio":"na","channel":36,"max_txpower":26}]}]}
            """;
        var overlaid = (JsonNode.Parse(ImpactAnalyzer.OverlayDevices(state, live))!["data"] as JsonArray)![0]!;
        overlaid["name"]!.GetValue<string>().Should().Be("Old name");
        overlaid["rx_bytes"]!.GetValue<int>().Should().Be(42);
        overlaid["radio_table"]![0]!["channel"]!.GetValue<int>().Should().Be(149);
        overlaid["radio_table"]![0]!["max_txpower"]!.GetValue<int>().Should().Be(26);
        overlaid["port_overrides"]!.AsArray().Should().HaveCount(1);
    }

    private static AuditIssue Issue(string title, AuditSeverity severity, string? network = null) => new()
    {
        Title = title, Severity = severity, Category = "Firewall", Description = "d", CurrentNetwork = network,
    };

    [Fact]
    public void Compare_AddedIsRegression_RemovedIsFix_WorstSeverityWins()
    {
        var before = new AuditResult { Issues = [Issue("Old", AuditSeverity.Recommended)] };
        var after = new AuditResult
        {
            Issues = [Issue("IoT Network Not Isolated", AuditSeverity.Recommended, "IoT"), Issue("Mgmt Open", AuditSeverity.Critical)],
        };
        var result = ImpactAnalyzer.Compare(before, after);
        result.Regressions.Should().HaveCount(2);
        result.Fixes.Should().ContainSingle(f => f.Title == "Old");
        result.WorstRegression.Should().Be(AuditSeverity.Critical);
        result.Regressions.Should().Contain(r => r.Title == "IoT Network Not Isolated" || r.Title == "IoT Network Not Isolated (IoT)");
    }

    [Fact]
    public void Compare_InformationalIsNotAnIssue()
    {
        var before = new AuditResult { Issues = [Issue("Note A", AuditSeverity.Informational)] };
        var after = new AuditResult { Issues = [Issue("Note B", AuditSeverity.Informational), Issue("Mgmt Open", AuditSeverity.Critical)] };
        var result = ImpactAnalyzer.Compare(before, after);
        result.AfterIssueCount.Should().Be(1);
        result.Regressions.Should().ContainSingle(r => r.Title == "Mgmt Open");
        result.Fixes.Should().BeEmpty();
    }

    [Fact]
    public void TriggerList_DerivesFromAuditInputs()
    {
        AuditConfigInputs.TriggerCollections.Should().Contain(["networkconf", "firewall_policy", "firewallgroup", "portconf", "setting", "nat", "device", "portforward", "firewall_zone"]);
        ImpactAnalyzer.Triggers(["static_dns"]).Should().BeFalse();
    }
}
