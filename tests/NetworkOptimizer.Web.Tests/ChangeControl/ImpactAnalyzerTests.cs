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

    [Fact]
    public void ZoneMembership_ANetworkMovedToANewZone_IsAuditedInIt()
    {
        // Set Up Zones moves IoT out of Internal into a zone the plan creates; the network's own
        // firewall_zone_id still names Internal, and the audit read its zone from that.
        var state = State(
            ("networkconf", """{"_id":"n1","name":"Default","firewall_zone_id":"z-internal"}"""),
            ("networkconf", """{"_id":"n2","name":"IoT","firewall_zone_id":"z-internal"}"""),
            ("firewall_zone", """{"_id":"z-internal","name":"Internal","network_ids":["n1"]}"""),
            ("firewall_zone", """{"_id":"@ref:firewall_zone:create:IoT","name":"IoT","network_ids":["n2"]}"""));

        var planned = ImpactAnalyzer.WithZoneMembership(state);

        planned.Get("networkconf").Single(n => n["name"]!.ToString() == "IoT")["firewall_zone_id"]!.ToString()
            .Should().Be("@ref:firewall_zone:create:IoT");
        planned.Get("networkconf").Single(n => n["name"]!.ToString() == "Default")["firewall_zone_id"]!.ToString().Should().Be("z-internal");
        // The plan's own state is left as it is: only the audit's copy moves.
        state.Get("networkconf").Single(n => n["name"]!.ToString() == "IoT")["firewall_zone_id"]!.ToString().Should().Be("z-internal");
    }

    [Fact]
    public void ZoneMembership_NothingMoved_IsTheSameState()
    {
        var state = State(
            ("networkconf", """{"_id":"n1","name":"Default","firewall_zone_id":"z-internal"}"""),
            ("firewall_zone", """{"_id":"z-internal","name":"Internal","network_ids":["n1"]}"""));

        ImpactAnalyzer.WithZoneMembership(state).Should().BeSameAs(state);
    }

    [Fact]
    public void Overlay_PortTableTakesTheStatesOverrides()
    {
        // The state's port 4 is on a profile; live has since been set to explicit settings.
        var state = State(("device", """
            {"_id":"d1","mac":"aa:bb:cc:dd:ee:01","port_overrides":[{"port_idx":4,"portconf_id":"p-disabled"}]}
            """));
        var live = """
            {"meta":{"rc":"ok"},"data":[{"_id":"d1","mac":"aa:bb:cc:dd:ee:01",
             "port_overrides":[{"port_idx":4,"forward":"all","native_networkconf_id":"n1","tagged_vlan_mgmt":"auto"}],
             "port_table":[{"port_idx":4,"forward":"all","native_networkconf_id":"n1","tagged_vlan_mgmt":"auto","up":true,"poe_mode":"auto"},
                           {"port_idx":5,"forward":"native","up":false}]}]}
            """;
        var ports = (JsonNode.Parse(ImpactAnalyzer.OverlayDevices(state, live))!["data"] as JsonArray)![0]!["port_table"]!.AsArray();
        ports[0]!["portconf_id"]!.GetValue<string>().Should().Be("p-disabled");
        ports[0]!.AsObject().ContainsKey("forward").Should().BeFalse();
        ports[0]!.AsObject().ContainsKey("native_networkconf_id").Should().BeFalse();
        ports[0]!["up"]!.GetValue<bool>().Should().BeTrue();
        ports[0]!["poe_mode"]!.GetValue<string>().Should().Be("auto");
        ports[1]!["forward"]!.GetValue<string>().Should().Be("native");
    }

    [Fact]
    public void Overlay_NetworkTableTakesTheStatesNetworkconf()
    {
        // A re-addressed state: the gateway's network_table still shows the live subnet and DHCP DNS.
        var state = State(
            ("device", """{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","name":"Gateway"}"""),
            ("networkconf", """
                {"_id":"n1","name":"IoT","ip_subnet":"203.0.113.1/24","dhcpd_dns_enabled":true,"dhcpd_dns_1":"203.0.113.53"}
                """));
        var live = """
            {"meta":{"rc":"ok"},"data":[{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","type":"udm",
             "network_table":[{"_id":"n1","name":"IoT","ip_subnet":"192.0.2.1/24","dhcpd_dns_enabled":true,"dhcpd_dns_1":"192.0.2.53","num_sta":7},
                              {"_id":"n2","name":"Default","ip_subnet":"198.51.100.1/24"}]}]}
            """;
        var table = (JsonNode.Parse(ImpactAnalyzer.OverlayDevices(state, live))!["data"] as JsonArray)![0]!["network_table"]!.AsArray();
        table[0]!["ip_subnet"]!.GetValue<string>().Should().Be("203.0.113.1/24");
        table[0]!["dhcpd_dns_1"]!.GetValue<string>().Should().Be("203.0.113.53");
        table[0]!["num_sta"]!.GetValue<int>().Should().Be(7);
        table[1]!["ip_subnet"]!.GetValue<string>().Should().Be("198.51.100.1/24");
    }

    [Fact]
    public void Moves_AuditGeneratedObjectsClientsAndDevicesAtTheNewAddresses()
    {
        var before = State(("networkconf", """{"_id":"n1","name":"IoT","ip_subnet":"192.0.2.1/24"}"""));
        var after = State(
            ("networkconf", """{"_id":"n1","name":"IoT","ip_subnet":"203.0.113.1/24"}"""),
            ("firewall_policy", """
                {"_id":"p1","name":"Block 192.0.2.0/24 Internet Access","origin_type":"network","source":{"ips":["192.0.2.0/24"]}}
                """),
            ("device", """{"_id":"d1","mac":"aa:bb:cc:dd:ee:01"}"""));
        var live = """{"meta":{"rc":"ok"},"data":[{"_id":"d1","mac":"aa:bb:cc:dd:ee:01","ip":"192.0.2.20"}]}""";

        var moves = ImpactAnalyzer.SubnetMoves(before, after);
        moves.Should().ContainSingle();
        var inputs = ImpactAnalyzer.BuildInputs(after, live, moves: moves);

        inputs.FirewallPoliciesData!.Value[0].GetProperty("source").GetProperty("ips")[0].GetString().Should().Be("203.0.113.0/24");
        (JsonNode.Parse(inputs.DeviceDataJson)!["data"] as JsonArray)![0]!["ip"]!.GetValue<string>().Should().Be("203.0.113.20");
        inputs.MapClientIp!("192.0.2.31").Should().Be("203.0.113.31");
        inputs.MapClientIp!("198.51.100.7").Should().BeNull();
        ImpactAnalyzer.BuildInputs(before, live).MapClientIp.Should().BeNull();
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
