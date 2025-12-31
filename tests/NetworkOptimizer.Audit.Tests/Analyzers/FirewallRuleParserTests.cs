using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class FirewallRuleParserTests
{
    private readonly FirewallRuleParser _parser;
    private readonly Mock<ILogger<FirewallRuleParser>> _loggerMock;

    public FirewallRuleParserTests()
    {
        _loggerMock = new Mock<ILogger<FirewallRuleParser>>();
        _parser = new FirewallRuleParser(_loggerMock.Object);
    }

    #region ExtractFirewallRules Tests

    [Fact]
    public void ExtractFirewallRules_EmptyArray_ReturnsEmptyList()
    {
        var json = JsonDocument.Parse("[]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFirewallRules_NonGatewayDevice_ReturnsEmptyList()
    {
        var json = JsonDocument.Parse(@"[{""type"": ""usw"", ""name"": ""Switch""}]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFirewallRules_GatewayWithNoRules_ReturnsEmptyList()
    {
        var json = JsonDocument.Parse(@"[{""type"": ""ugw"", ""name"": ""Gateway""}]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFirewallRules_GatewayWithRules_ReturnsRules()
    {
        var json = JsonDocument.Parse(@"[{
            ""type"": ""ugw"",
            ""name"": ""Gateway"",
            ""firewall_rules"": [{
                ""_id"": ""rule1"",
                ""name"": ""Block All"",
                ""action"": ""drop"",
                ""enabled"": true
            }]
        }]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be("rule1");
        rules[0].Name.Should().Be("Block All");
        rules[0].Action.Should().Be("drop");
    }

    [Fact]
    public void ExtractFirewallRules_UdmDevice_ReturnsRules()
    {
        var json = JsonDocument.Parse(@"[{
            ""type"": ""udm"",
            ""firewall_rules"": [{
                ""_id"": ""rule1"",
                ""name"": ""Allow DNS"",
                ""action"": ""accept""
            }]
        }]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().ContainSingle();
        rules[0].Name.Should().Be("Allow DNS");
    }

    [Fact]
    public void ExtractFirewallRules_UxgDevice_ReturnsRules()
    {
        var json = JsonDocument.Parse(@"[{
            ""type"": ""uxg"",
            ""firewall_rules"": [{
                ""_id"": ""rule1"",
                ""action"": ""accept""
            }]
        }]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().ContainSingle();
    }

    [Fact]
    public void ExtractFirewallRules_WrappedDataResponse_ReturnsRules()
    {
        var json = JsonDocument.Parse(@"{
            ""data"": [{
                ""type"": ""ugw"",
                ""firewall_rules"": [{
                    ""_id"": ""rule1"",
                    ""name"": ""Test Rule""
                }]
            }]
        }").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().ContainSingle();
        rules[0].Name.Should().Be("Test Rule");
    }

    [Fact]
    public void ExtractFirewallRules_SingleDevice_ReturnsRules()
    {
        var json = JsonDocument.Parse(@"{
            ""type"": ""udm"",
            ""firewall_rules"": [{
                ""_id"": ""single-rule"",
                ""name"": ""Single Device Rule""
            }]
        }").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be("single-rule");
    }

    [Fact]
    public void ExtractFirewallRules_MultipleRules_ReturnsAll()
    {
        var json = JsonDocument.Parse(@"[{
            ""type"": ""ugw"",
            ""firewall_rules"": [
                {""_id"": ""rule1"", ""name"": ""Rule 1""},
                {""_id"": ""rule2"", ""name"": ""Rule 2""},
                {""_id"": ""rule3"", ""name"": ""Rule 3""}
            ]
        }]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().HaveCount(3);
    }

    [Fact]
    public void ExtractFirewallRules_DeviceWithoutType_SkipsDevice()
    {
        var json = JsonDocument.Parse(@"[{
            ""name"": ""Unknown Device"",
            ""firewall_rules"": [{""_id"": ""rule1""}]
        }]").RootElement;

        var rules = _parser.ExtractFirewallRules(json);

        rules.Should().BeEmpty();
    }

    #endregion

    #region ExtractFirewallPolicies Tests

    [Fact]
    public void ExtractFirewallPolicies_NullData_ReturnsEmptyList()
    {
        JsonElement? nullData = null;

        var rules = _parser.ExtractFirewallPolicies(nullData);

        rules.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFirewallPolicies_EmptyArray_ReturnsEmptyList()
    {
        var json = JsonDocument.Parse("[]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFirewallPolicies_ValidPolicy_ReturnsRule()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""policy1"",
            ""name"": ""Allow HTTPS"",
            ""enabled"": true,
            ""action"": ""allow"",
            ""protocol"": ""tcp"",
            ""index"": 10
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be("policy1");
        rules[0].Name.Should().Be("Allow HTTPS");
        rules[0].Enabled.Should().BeTrue();
        rules[0].Action.Should().Be("allow");
        rules[0].Protocol.Should().Be("tcp");
        rules[0].Index.Should().Be(10);
    }

    [Fact]
    public void ExtractFirewallPolicies_WrappedDataResponse_ReturnsRules()
    {
        var json = JsonDocument.Parse(@"{
            ""data"": [{
                ""_id"": ""wrapped-policy"",
                ""name"": ""Wrapped Policy""
            }]
        }").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be("wrapped-policy");
    }

    [Fact]
    public void ExtractFirewallPolicies_WithSourceNetworkIds_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""policy1"",
            ""name"": ""Inter-VLAN Block"",
            ""action"": ""drop"",
            ""source"": {
                ""matching_target"": ""network"",
                ""network_ids"": [""net-iot"", ""net-guest""]
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].SourceNetworkIds.Should().Contain("net-iot");
        rules[0].SourceNetworkIds.Should().Contain("net-guest");
        rules[0].SourceMatchingTarget.Should().Be("network");
    }

    [Fact]
    public void ExtractFirewallPolicies_WithWebDomains_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""policy1"",
            ""name"": ""Allow Cloud Access"",
            ""action"": ""allow"",
            ""destination"": {
                ""web_domains"": [""ui.com"", ""unifi.ui.com""]
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].WebDomains.Should().Contain("ui.com");
        rules[0].WebDomains.Should().Contain("unifi.ui.com");
    }

    [Fact]
    public void ExtractFirewallPolicies_WithDestinationPort_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""policy1"",
            ""name"": ""Block DNS"",
            ""destination"": {
                ""port"": ""53""
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].DestinationPort.Should().Be("53");
    }

    [Fact]
    public void ExtractFirewallPolicies_WithSourceIps_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""policy1"",
            ""name"": ""Allow Specific IPs"",
            ""source"": {
                ""ips"": [""192.168.1.100"", ""192.168.1.101""]
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].SourceIps.Should().Contain("192.168.1.100");
        rules[0].SourceIps.Should().Contain("192.168.1.101");
    }

    [Fact]
    public void ExtractFirewallPolicies_WithClientMacs_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""policy1"",
            ""name"": ""Block MAC"",
            ""source"": {
                ""client_macs"": [""aa:bb:cc:dd:ee:ff""]
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].SourceClientMacs.Should().Contain("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void ExtractFirewallPolicies_PredefinedRule_MarksAsPredefined()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""predefined1"",
            ""name"": ""System Rule"",
            ""predefined"": true
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].Predefined.Should().BeTrue();
    }

    [Fact]
    public void ExtractFirewallPolicies_DisabledRule_MarksAsDisabled()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""disabled1"",
            ""name"": ""Disabled Rule"",
            ""enabled"": false
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].Enabled.Should().BeFalse();
    }

    [Fact]
    public void ExtractFirewallPolicies_WithZoneIds_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""zone-policy"",
            ""name"": ""Zone Rule"",
            ""source"": {
                ""zone_id"": ""zone-internal""
            },
            ""destination"": {
                ""zone_id"": ""zone-external""
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].SourceZoneId.Should().Be("zone-internal");
        rules[0].DestinationZoneId.Should().Be("zone-external");
    }

    [Fact]
    public void ExtractFirewallPolicies_WithMatchOppositeFlags_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""opposite-policy"",
            ""name"": ""Opposite Match Rule"",
            ""source"": {
                ""match_opposite_ips"": true,
                ""match_opposite_networks"": true
            },
            ""destination"": {
                ""match_opposite_ips"": true,
                ""match_opposite_networks"": true,
                ""match_opposite_ports"": true
            }
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].SourceMatchOppositeIps.Should().BeTrue();
        rules[0].SourceMatchOppositeNetworks.Should().BeTrue();
        rules[0].DestinationMatchOppositeIps.Should().BeTrue();
        rules[0].DestinationMatchOppositeNetworks.Should().BeTrue();
        rules[0].DestinationMatchOppositePorts.Should().BeTrue();
    }

    [Fact]
    public void ExtractFirewallPolicies_WithIcmpTypename_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"[{
            ""_id"": ""icmp-policy"",
            ""name"": ""Block Ping"",
            ""protocol"": ""icmp"",
            ""icmp_typename"": ""echo-request""
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().ContainSingle();
        rules[0].IcmpTypename.Should().Be("echo-request");
    }

    [Fact]
    public void ExtractFirewallPolicies_MissingId_SkipsRule()
    {
        var json = JsonDocument.Parse(@"[{
            ""name"": ""No ID Rule"",
            ""action"": ""drop""
        }]").RootElement;

        var rules = _parser.ExtractFirewallPolicies(json);

        rules.Should().BeEmpty();
    }

    #endregion

    #region ParseFirewallRule (Legacy Format) Tests

    [Fact]
    public void ParseFirewallRule_ValidRule_ReturnsRule()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""legacy1"",
            ""name"": ""Legacy Rule"",
            ""action"": ""accept"",
            ""enabled"": true,
            ""rule_index"": 5,
            ""protocol"": ""tcp""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.Id.Should().Be("legacy1");
        rule.Name.Should().Be("Legacy Rule");
        rule.Action.Should().Be("accept");
        rule.Enabled.Should().BeTrue();
        rule.Index.Should().Be(5);
        rule.Protocol.Should().Be("tcp");
    }

    [Fact]
    public void ParseFirewallRule_MissingId_ReturnsNull()
    {
        var json = JsonDocument.Parse(@"{
            ""name"": ""No ID"",
            ""action"": ""drop""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().BeNull();
    }

    [Fact]
    public void ParseFirewallRule_RuleIdProperty_ParsesId()
    {
        var json = JsonDocument.Parse(@"{
            ""rule_id"": ""alt-id"",
            ""name"": ""Alt ID Rule""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.Id.Should().Be("alt-id");
    }

    [Fact]
    public void ParseFirewallRule_WithSourceInfo_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""src-rule"",
            ""src_type"": ""network"",
            ""src_address"": ""192.168.1.0/24"",
            ""src_port"": ""80""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.SourceType.Should().Be("network");
        rule.Source.Should().Be("192.168.1.0/24");
        rule.SourcePort.Should().Be("80");
    }

    [Fact]
    public void ParseFirewallRule_WithDestinationInfo_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""dst-rule"",
            ""dst_type"": ""address"",
            ""dst_address"": ""10.0.0.0/8"",
            ""dst_port"": ""443""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.DestinationType.Should().Be("address");
        rule.Destination.Should().Be("10.0.0.0/8");
        rule.DestinationPort.Should().Be("443");
    }

    [Fact]
    public void ParseFirewallRule_WithNetworkId_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""net-rule"",
            ""src_network_id"": ""net-corporate"",
            ""dst_network_id"": ""net-iot""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.Source.Should().Be("net-corporate");
        rule.Destination.Should().Be("net-iot");
        rule.SourceNetworkIds.Should().Contain("net-corporate");
    }

    [Fact]
    public void ParseFirewallRule_WithHitCount_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""hit-rule"",
            ""hit_count"": 1000
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.HitCount.Should().Be(1000);
        rule.HasBeenHit.Should().BeTrue();
    }

    [Fact]
    public void ParseFirewallRule_ZeroHitCount_HasBeenHitFalse()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""no-hit-rule"",
            ""hit_count"": 0
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.HasBeenHit.Should().BeFalse();
    }

    [Fact]
    public void ParseFirewallRule_WithRuleset_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""ruleset-rule"",
            ""ruleset"": ""WAN_IN""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.Ruleset.Should().Be("WAN_IN");
    }

    [Fact]
    public void ParseFirewallRule_WithNestedSourceNetworkIds_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""nested-src"",
            ""source"": {
                ""network_ids"": [""net1"", ""net2""]
            }
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.SourceNetworkIds.Should().Contain("net1");
        rule.SourceNetworkIds.Should().Contain("net2");
    }

    [Fact]
    public void ParseFirewallRule_WithNestedWebDomains_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""web-rule"",
            ""destination"": {
                ""web_domains"": [""example.com"", ""test.com""]
            }
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.WebDomains.Should().Contain("example.com");
        rule.WebDomains.Should().Contain("test.com");
    }

    [Fact]
    public void ParseFirewallRule_DisabledRule_ParsesCorrectly()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""disabled"",
            ""enabled"": false
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ParseFirewallRule_MissingEnabled_DefaultsToTrue()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""no-enabled"",
            ""name"": ""No Enabled Field""
        }").RootElement;

        var rule = _parser.ParseFirewallRule(json);

        rule.Should().NotBeNull();
        rule!.Enabled.Should().BeTrue();
    }

    #endregion

    #region ParseFirewallPolicy Tests

    [Fact]
    public void ParseFirewallPolicy_ValidPolicy_ReturnsRule()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""policy1"",
            ""name"": ""Test Policy"",
            ""action"": ""allow"",
            ""enabled"": true,
            ""index"": 1
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().NotBeNull();
        rule!.Id.Should().Be("policy1");
        rule.Name.Should().Be("Test Policy");
        rule.Action.Should().Be("allow");
        rule.Enabled.Should().BeTrue();
        rule.Index.Should().Be(1);
    }

    [Fact]
    public void ParseFirewallPolicy_MissingId_ReturnsNull()
    {
        var json = JsonDocument.Parse(@"{
            ""name"": ""No ID Policy""
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().BeNull();
    }

    [Fact]
    public void ParseFirewallPolicy_EmptyId_ReturnsNull()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": """",
            ""name"": ""Empty ID Policy""
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().BeNull();
    }

    [Fact]
    public void ParseFirewallPolicy_FullSourceInfo_ParsesAllFields()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""full-source"",
            ""source"": {
                ""matching_target"": ""network"",
                ""port"": ""8080"",
                ""zone_id"": ""internal-zone"",
                ""match_opposite_ips"": true,
                ""match_opposite_networks"": true,
                ""network_ids"": [""net1""],
                ""ips"": [""10.0.0.1""],
                ""client_macs"": [""00:11:22:33:44:55""]
            }
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().NotBeNull();
        rule!.SourceMatchingTarget.Should().Be("network");
        rule.SourcePort.Should().Be("8080");
        rule.SourceZoneId.Should().Be("internal-zone");
        rule.SourceMatchOppositeIps.Should().BeTrue();
        rule.SourceMatchOppositeNetworks.Should().BeTrue();
        rule.SourceNetworkIds.Should().Contain("net1");
        rule.SourceIps.Should().Contain("10.0.0.1");
        rule.SourceClientMacs.Should().Contain("00:11:22:33:44:55");
    }

    [Fact]
    public void ParseFirewallPolicy_FullDestinationInfo_ParsesAllFields()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""full-dest"",
            ""destination"": {
                ""port"": ""443"",
                ""matching_target"": ""address"",
                ""zone_id"": ""external-zone"",
                ""match_opposite_ips"": true,
                ""match_opposite_networks"": true,
                ""match_opposite_ports"": true,
                ""web_domains"": [""example.com""],
                ""network_ids"": [""net2""],
                ""ips"": [""8.8.8.8""]
            }
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().NotBeNull();
        rule!.DestinationPort.Should().Be("443");
        rule.DestinationMatchingTarget.Should().Be("address");
        rule.DestinationZoneId.Should().Be("external-zone");
        rule.DestinationMatchOppositeIps.Should().BeTrue();
        rule.DestinationMatchOppositeNetworks.Should().BeTrue();
        rule.DestinationMatchOppositePorts.Should().BeTrue();
        rule.WebDomains.Should().Contain("example.com");
        rule.DestinationNetworkIds.Should().Contain("net2");
        rule.DestinationIps.Should().Contain("8.8.8.8");
    }

    [Fact]
    public void ParseFirewallPolicy_EnabledDefaultsToTrue()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""default-enabled""
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().NotBeNull();
        rule!.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ParseFirewallPolicy_IndexDefaultsToZero()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""default-index""
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().NotBeNull();
        rule!.Index.Should().Be(0);
    }

    [Fact]
    public void ParseFirewallPolicy_PredefinedDefaultsToFalse()
    {
        var json = JsonDocument.Parse(@"{
            ""_id"": ""default-predefined""
        }").RootElement;

        var rule = _parser.ParseFirewallPolicy(json);

        rule.Should().NotBeNull();
        rule!.Predefined.Should().BeFalse();
    }

    #endregion
}
