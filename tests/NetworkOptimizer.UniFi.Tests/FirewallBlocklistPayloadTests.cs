using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// The write bodies Network Optimizer sends for gateway blocking, checked against what UniFi Network's
/// own UI sends on 10.6 and 11.x (captured 2026-10-11).
/// </summary>
public class FirewallBlocklistPayloadTests
{
    [Fact]
    public void GroupWrite_MatchesTheUiBody()
    {
        var json = JsonSerializer.Serialize(new UniFiFirewallGroupWrite(null, "Blocklist (IPv4)", "address-group", ["203.0.113.7"], "d"));

        json.Should().Be("""{"_id":null,"name":"Blocklist (IPv4)","group_type":"address-group","group_members":["203.0.113.7"],"description":"d","source":"static"}""");
    }

    [Fact]
    public void GroupRead_MissingMembersIsEmpty()
    {
        // An emptied group comes back without group_members at all.
        var group = JsonSerializer.Deserialize<UniFiFirewallGroup>(
            """{"_id":"8476be0c-cdd1-449d-93d4-b101fed8f128","name":"x","group_type":"address-group","source":"static"}""")!;

        group.GroupMembers.Should().BeEmpty();
        group.Id.Should().Be("8476be0c-cdd1-449d-93d4-b101fed8f128");
    }

    [Fact]
    public void BlockPolicy_MatchesTheUiBody_WithoutReportFlow()
    {
        var policy = UniFiGroupBlockPolicyWrite.Create("Network Optimizer Blocklist", "d", "IPV6", "ext", "grp", "int");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(policy));
        var root = doc.RootElement;

        root.GetProperty("action").GetString().Should().Be("BLOCK");
        root.GetProperty("ip_version").GetString().Should().Be("IPV6");
        root.GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("connection_state_type").GetString().Should().Be("ALL");
        root.TryGetProperty("connection_states", out _).Should().BeFalse("the body is unchanged unless states are narrowed");
        root.GetProperty("schedule").GetProperty("mode").GetString().Should().Be("ALWAYS");
        root.TryGetProperty("report_flow", out _).Should().BeFalse("Network 10.x has no report_flow field");

        var source = root.GetProperty("source");
        source.GetProperty("zone_id").GetString().Should().Be("ext");
        source.GetProperty("matching_target").GetString().Should().Be("IP");
        source.GetProperty("matching_target_type").GetString().Should().Be("OBJECT");
        source.GetProperty("ip_group_id").GetString().Should().Be("grp");
        var destination = root.GetProperty("destination");
        destination.GetProperty("zone_id").GetString().Should().Be("int");
        destination.GetProperty("matching_target").GetString().Should().Be("ANY");
    }

    [Fact]
    public void BlockPolicy_NewConnectionsOnly_UsesTheCustomStateShape()
    {
        // The shape UniFi Network stores for its predefined Block Invalid Traffic, with NEW instead.
        var policy = UniFiGroupBlockPolicyWrite.Create("Blocklist", "d", "IPV4", "ext", "grp", "int", newConnectionsOnly: true);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(policy));

        doc.RootElement.GetProperty("connection_state_type").GetString().Should().Be("CUSTOM");
        doc.RootElement.GetProperty("connection_states").EnumerateArray().Select(e => e.GetString()).Should().Equal("NEW");
    }

    [Fact]
    public void PolicyRead_ParsesConnectionStates()
    {
        var p = JsonSerializer.Deserialize<UniFiFirewallPolicy>(
            """{"_id":"x","connection_state_type":"CUSTOM","connection_states":["INVALID"],"source":{},"destination":{}}""")!;

        p.ConnectionStateType.Should().Be("CUSTOM");
        p.ConnectionStates.Should().Equal("INVALID");
    }

    [Fact]
    public void PolicyRead_ParsesTheFieldsBlockingUses()
    {
        var policies = JsonSerializer.Deserialize<List<UniFiFirewallPolicy>>("""
            [{"_id":"6acbcad69ecd8986baeb7b06","action":"BLOCK","enabled":true,"index":10001,"ip_version":"IPV4","name":"n","predefined":false,
              "source":{"ip_group_id":"6acbca8b9ecd8986baeb7a1b","matching_target":"IP","matching_target_type":"OBJECT","zone_id":"ext"},
              "destination":{"matching_target":"ANY","zone_id":"int"}}]
            """)!;

        var p = policies.Single();
        p.Index.Should().Be(10001);
        p.Source.IpGroupId.Should().Be("6acbca8b9ecd8986baeb7a1b");
        p.Destination.ZoneId.Should().Be("int");
        p.Predefined.Should().BeFalse();
    }
    [Fact]
    public void OutboundPolicy_MatchesTheGroupAsDestination()
    {
        var policy = UniFiGroupBlockPolicyWrite.CreateToGroup("Out", "d", "IPV4", "int", "ext", "grp");
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(policy));
        var root = doc.RootElement;

        root.GetProperty("action").GetString().Should().Be("BLOCK");
        root.GetProperty("source").GetProperty("zone_id").GetString().Should().Be("int");
        root.GetProperty("source").GetProperty("matching_target").GetString().Should().Be("ANY");
        var dst = root.GetProperty("destination");
        dst.GetProperty("zone_id").GetString().Should().Be("ext");
        dst.GetProperty("matching_target").GetString().Should().Be("IP");
        dst.GetProperty("matching_target_type").GetString().Should().Be("OBJECT");
        dst.GetProperty("ip_group_id").GetString().Should().Be("grp");
        dst.GetProperty("match_opposite_ips").GetBoolean().Should().BeFalse();
    }
}