using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class ModemFirewallAccessTests
{
    [Fact]
    public void UnrestrictedModemAllowBeforeEgressBlocks_DoesNotReportMissingAccess()
    {
        var rules = new List<FirewallRule>
        {
            CreateBlock("dns", index: 100),
            CreateBlock("dot", index: 110),
            CreateBlock("ntp", index: 120),
            CreateBlock(index: 130),
            CreateAllow()
        };

        MissingAccess(rules).Should().BeFalse();
    }

    [Fact]
    public void PredefinedAllowAllDoesNotEstablishAccessWhenInternetIsDisabled()
    {
        MissingAccess([CreateAllow("predefined-any")]).Should().BeTrue();
        MissingAccess([CreateAllow("predefined-any"), CreateAllow(), CreateBlock()]).Should().BeFalse();
        MissingAccess([CreateAllow("predefined-any"), CreateAllow("different-mac"), CreateBlock()])
            .Should().BeTrue();
    }

    [Fact]
    public void PredefinedBlockStillEclipsesModemException()
    {
        MissingAccess([CreateAllow(), CreateBlock(index: 5, predefined: true)]).Should().BeTrue();
    }

    [Theory]
    [InlineData("different-mac", true)]
    [InlineData("disabled", true)]
    [InlineData("udp", true)]
    [InlineData("http-only", true)]
    [InlineData("excluded-https", true)]
    [InlineData("excluded-tcp", true)]
    [InlineData("source-port", true)]
    [InlineData("different-source-zone", true)]
    [InlineData("different-destination-zone", true)]
    [InlineData("destination-ip", true)]
    [InlineData("destination-network", true)]
    [InlineData("unrelated-domain", true)]
    [InlineData("unresolved-domain", true)]
    [InlineData("unresolved-port", true)]
    [InlineData("app", true)]
    [InlineData("respond-only", true)]
    [InlineData("established-only", true)]
    [InlineData("ipv6", true)]
    [InlineData("ipv4", false)]
    [InlineData("new-state", false)]
    [InlineData("ip", false)]
    [InlineData("cidr", false)]
    [InlineData("different-ip", true)]
    [InlineData("excluded-ip", true)]
    [InlineData("other-excluded-ip", false)]
    [InlineData("network", false)]
    [InlineData("any", false)]
    [InlineData("domain", false)]
    [InlineData("domain-network", false)]
    [InlineData("domain-ip", false)]
    [InlineData("domain-different-mac", true)]
    [InlineData("domain-http-only", true)]
    public void AllowMustCoverActualModemRegistration(string variant, bool expectedFinding)
    {
        MissingAccess([CreateAllow(variant), CreateBlock()]).Should().Be(expectedFinding);
    }

    [Theory]
    [InlineData("all", true)]
    [InlineData("mac", true)]
    [InlineData("ip", true)]
    [InlineData("network", true)]
    [InlineData("https", true)]
    [InlineData("domain", true)]
    [InlineData("different-mac", false)]
    [InlineData("different-ip", false)]
    [InlineData("different-zone", false)]
    [InlineData("internal", false)]
    [InlineData("ipv6", false)]
    [InlineData("disabled", false)]
    [InlineData("invalid-state", false)]
    [InlineData("dns", false)]
    [InlineData("dot", false)]
    [InlineData("ntp", false)]
    public void EarlierBlockMustMatchModemRegistration(string variant, bool expectedFinding)
    {
        MissingAccess([CreateAllow(), CreateBlock(variant, index: 5)])
            .Should().Be(expectedFinding);
    }

    [Fact]
    public void EveryModemOnManagementNetworkNeedsAccess()
    {
        var devices = new List<CellularModemIdentity>
        {
            Modem("192.0.2.5", "02:00:00:00:00:01"),
            Modem("192.0.2.6", "02:00:00:00:00:02")
        };

        MissingAccess([CreateAllow(), CreateBlock()], devices).Should().BeTrue();
        MissingAccess([CreateAllow("network"), CreateBlock()], devices).Should().BeFalse();
    }

    [Fact]
    public void ModemOnAnotherNetworkDoesNotRequireManagementException()
    {
        var other = new NetworkInfo
        {
            Id = "other",
            Name = "Other",
            VlanId = 20,
            Purpose = NetworkPurpose.Corporate,
            Subnet = "198.51.100.0/24"
        };
        CreateAnalyzer().AnalyzeManagementNetworkFirewallAccess([CreateBlock()], [Management(), other],
                has5GDevice: true, externalZoneId: "external",
                cellularDevices: [Modem("198.51.100.5", "02:00:00:00:00:02")])
            .Should().NotContain(i => i.Type == IssueTypes.MgmtMissing5gAccess);
    }

    [Fact]
    public void MissingInventoryDoesNotTreatUnverifiedDeviceExceptionAsCoverage()
    {
        var analyzer = CreateAnalyzer();
        analyzer.AnalyzeManagementNetworkFirewallAccess([CreateAllow("domain"), CreateBlock()],
                [Management()], has5GDevice: true, externalZoneId: "external")
            .Should().Contain(i => i.Type == IssueTypes.MgmtMissing5gAccess);
        MissingAccess([CreateAllow(), CreateBlock()],
            [Modem("", "02:00:00:00:00:01")]).Should().BeTrue();
    }

    [Fact]
    public void MissingMacCannotRuleOutEarlierDeviceSpecificBlock()
    {
        MissingAccess([CreateAllow("network"), CreateBlock("mac", index: 5)],
            [Modem("192.0.2.5", "")]).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "mac", true)]
    [InlineData("", "different-mac", false)]
    [InlineData("198.51.100.5", "mac", true)]
    [InlineData("198.51.100.5", "different-mac", false)]
    [InlineData("198.51.100.5", "ip", false)]
    [InlineData("198.51.100.5", "unplaced-ip", true)]
    [InlineData("", "all", true)]
    public void UnknownPlacementRetainsKnownIdentityForBlocks(string ip, string block, bool expectedFinding)
    {
        MissingAccess([CreateAllow("network"), CreateBlock(block, index: 5)],
            [Modem(ip, "02:00:00:00:00:01")]).Should().Be(expectedFinding);
    }

    [Fact]
    public void UnknownPlacementDoesNotEstablishAccessFromDeviceException()
    {
        MissingAccess([CreateAllow(), CreateBlock()],
            [Modem("198.51.100.5", "02:00:00:00:00:01")]).Should().BeTrue();
    }

    [Fact]
    public void UnknownPlacementAndMissingMacRetainConservativeBlockMatching()
    {
        MissingAccess([CreateAllow("network"), CreateBlock("mac", index: 5)],
            [Modem("", "")]).Should().BeTrue();
    }

    [Theory]
    [InlineData("MAC", "macs")]
    [InlineData("CLIENT", "client_macs")]
    public void ParsedDeviceSourcesUseCanonicalMacMatching(string target, string field)
    {
        var analyzer = CreateAnalyzer();
        var policy = JsonSerializer.Deserialize<JsonElement>($$"""
            {
              "_id": "synthetic-modem-allow",
              "enabled": true,
              "index": 10,
              "action": "ALLOW",
              "protocol": "all",
              "ip_version": "BOTH",
              "source": {
                "zone_id": "management-zone",
                "matching_target": "{{target}}",
                "{{field}}": ["02:00:00:00:00:01"]
              },
              "destination": {"zone_id": "external", "matching_target": "ANY"}
            }
            """);
        var rule = analyzer.ParseFirewallPolicy(policy);

        rule.Should().NotBeNull();
        MissingAccess([rule!, CreateBlock()]).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FullAuditPassesModemInventoryToFirewallCheck(bool differentDevice, bool predefinedOnly)
    {
        var engine = new ConfigAuditEngine(
            NullLogger<ConfigAuditEngine>.Instance, NullLoggerFactory.Instance);
        var result = await engine.RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = """
                [
                  {
                    "type": "udm", "model": "UDM-PRO", "name": "Synthetic gateway",
                    "mac": "02:00:00:00:00:10", "ip": "192.0.2.1",
                    "network_table": [{
                      "_id": "management", "name": "Management", "vlan": 99,
                      "ip_subnet": "192.0.2.1/24", "purpose": "corporate",
                      "internet_access_enabled": false, "firewall_zone_id": "management-zone"
                    }]
                  },
                  {
                    "type": "umbb", "model": "U5G", "name": "Synthetic modem",
                    "mac": "02:00:00:00:00:01", "ip": "192.0.2.5"
                  }
                ]
                """,
            NetworkPurposeOverrides = new() { ["management"] = "Management" },
            FirewallRules = predefinedOnly
                ? [CreateAllow("predefined-any")]
                : [CreateAllow("predefined-any"), CreateAllow(differentDevice ? "different-mac" : "mac"), CreateBlock()],
            FirewallZones =
            [
                new UniFiFirewallZone
                {
                    Id = "external", Name = "External", ZoneKey = FirewallZoneKeys.External
                }
            ]
        });

        result.Networks.Should().Contain(n => n.Id == "management" && n.Purpose == NetworkPurpose.Management);
        result.Issues.Any(i => i.Type == IssueTypes.MgmtMissing5gAccess).Should().Be(differentDevice || predefinedOnly);
    }

    private static bool MissingAccess(List<FirewallRule> rules, List<CellularModemIdentity>? devices = null) =>
        CreateAnalyzer().AnalyzeManagementNetworkFirewallAccess(rules, [Management()],
                has5GDevice: true, externalZoneId: "external",
                cellularDevices: devices ?? [Modem("192.0.2.5", "02:00:00:00:00:01")])
            .Any(i => i.Type == IssueTypes.MgmtMissing5gAccess);

    private static FirewallRuleAnalyzer CreateAnalyzer() => new(
        NullLogger<FirewallRuleAnalyzer>.Instance,
        new FirewallRuleParser(NullLogger<FirewallRuleParser>.Instance));

    private static CellularModemIdentity Modem(string ip, string mac) => new(ip, mac);

    private static NetworkInfo Management() => new()
    {
        Id = "management",
        Name = "Management",
        VlanId = 99,
        Purpose = NetworkPurpose.Management,
        Subnet = "192.0.2.0/24",
        FirewallZoneId = "management-zone",
        InternetAccessEnabled = false
    };

    private static FirewallRule CreateAllow(string variant = "mac") => new()
    {
        Id = "allow-modem",
        Enabled = variant != "disabled",
        Predefined = variant == "predefined-any",
        Index = 10,
        Action = "allow",
        SourceZoneId = variant == "different-source-zone" ? "other-zone" : "management-zone",
        SourceMatchingTarget = variant switch
        {
            "ip" or "cidr" or "different-ip" or "excluded-ip" or "other-excluded-ip" or "domain-ip" => "IP",
            "network" or "domain-network" => "NETWORK",
            "any" or "predefined-any" => "ANY",
            _ => "CLIENT"
        },
        SourceClientMacs = [variant.Contains("different-mac") ? "02:00:00:00:00:02" : "02:00:00:00:00:01"],
        SourceIps = [variant switch
        {
            "cidr" => "192.0.2.0/28",
            "different-ip" or "other-excluded-ip" => "192.0.2.6",
            _ => "192.0.2.5"
        }],
        SourceMatchOppositeIps = variant is "excluded-ip" or "other-excluded-ip",
        SourceNetworkIds = ["management"],
        DestinationZoneId = variant == "different-destination-zone" ? "internal" : "external",
        DestinationMatchingTarget = variant switch
        {
            "destination-ip" => "IP",
            "destination-network" => "NETWORK",
            _ when variant.StartsWith("domain") || variant == "unrelated-domain" => "WEB",
            _ => "ANY"
        },
        DestinationIps = variant == "destination-ip" ? ["203.0.113.5"] : null,
        DestinationNetworkIds = variant == "destination-network" ? ["other"] : null,
        WebDomains = variant.StartsWith("domain") ? ["t-mobile.com"] :
            variant == "unrelated-domain" ? ["example.com"] : null,
        Protocol = variant == "udp" ? "udp" : variant == "excluded-tcp" ? "tcp" : "all",
        MatchOppositeProtocol = variant == "excluded-tcp",
        DestinationPort = variant is "http-only" or "domain-http-only" ? "80" :
            variant == "excluded-https" ? "443" : null,
        DestinationMatchOppositePorts = variant == "excluded-https",
        SourcePort = variant == "source-port" ? "123" : null,
        HasUnresolvedDestinationDomainGroup = variant == "unresolved-domain",
        HasUnresolvedDestinationPortGroup = variant == "unresolved-port",
        AppIds = variant == "app" ? [12345] : null,
        IpVersion = variant == "ipv6" ? "IPV6" : variant == "ipv4" ? "IPV4" : "BOTH",
        ConnectionStateType = variant == "respond-only" ? "RESPOND_ONLY" :
            variant is "established-only" or "new-state" ? "CUSTOM" : "ALL",
        ConnectionStates = variant == "established-only" ? ["ESTABLISHED", "RELATED"] :
            variant == "new-state" ? ["NEW", "ESTABLISHED", "RELATED"] : null
    };

    private static FirewallRule CreateBlock(string variant = "all", int index = 100, bool predefined = false) => new()
    {
        Id = $"block-{variant}",
        Enabled = variant != "disabled",
        Predefined = predefined,
        Index = index,
        Action = "drop",
        SourceZoneId = variant == "different-zone" ? "other-zone" : "management-zone",
        SourceMatchingTarget = variant switch
        {
            "mac" or "different-mac" => "CLIENT",
            "ip" or "different-ip" or "unplaced-ip" => "IP",
            "network" => "NETWORK",
            _ => "ANY"
        },
        SourceClientMacs = [variant == "different-mac" ? "02:00:00:00:00:02" : "02:00:00:00:00:01"],
        SourceIps = [variant == "different-ip" ? "192.0.2.6" :
            variant == "unplaced-ip" ? "198.51.100.5" : "192.0.2.5"],
        SourceNetworkIds = ["management"],
        DestinationZoneId = variant == "internal" ? "internal" : "external",
        DestinationMatchingTarget = variant == "domain" ? "WEB" : "ANY",
        WebDomains = variant == "domain" ? ["t-mobile.com"] : null,
        Protocol = variant == "ntp" ? "udp" : "all",
        DestinationPort = variant switch
        {
            "dns" => "53",
            "dot" => "853",
            "ntp" => "123",
            "https" => "443",
            _ => null
        },
        IpVersion = variant == "ipv6" ? "IPV6" : "BOTH",
        ConnectionStateType = variant == "invalid-state" ? "CUSTOM" : "ALL",
        ConnectionStates = variant == "invalid-state" ? ["INVALID"] : null
    };
}
