using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class WanSteerValidationTests
{
    public class ValidateCidrListTests
    {
        [Fact]
        public void Accepts_valid_cidr()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"192.168.1.0/24\"]", "Test", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Accepts_bare_ip()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"10.0.0.1\"]", "Test", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Accepts_ip_range()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"192.168.1.1-192.168.1.50\"]", "Test", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Accepts_mixed_entries()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList(
                "[\"10.0.0.0/8\", \"192.168.1.1\", \"172.16.0.1-172.16.0.100\"]", "Test", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Rejects_invalid_format()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"not-an-ip\"]", "Source", errors);
            errors.Should().ContainSingle().Which.Should().Contain("not valid");
        }

        [Fact]
        public void Rejects_octet_out_of_range()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"999.0.0.1\"]", "Source", errors);
            errors.Should().ContainSingle().Which.Should().Contain("invalid IP octets");
        }

        [Fact]
        public void Rejects_prefix_out_of_range()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"10.0.0.0/33\"]", "Source", errors);
            errors.Should().ContainSingle().Which.Should().Contain("invalid prefix length");
        }

        [Fact]
        public void Handles_invalid_json()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("{bad json", "Source", errors);
            errors.Should().ContainSingle().Which.Should().Contain("format is invalid");
        }

        [Fact]
        public void Validates_range_endpoint_octets()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateCidrList("[\"192.168.1.1-192.168.1.300\"]", "Dest", errors);
            errors.Should().ContainSingle().Which.Should().Contain("invalid IP octets");
        }
    }

    public class ValidateMacListTests
    {
        [Fact]
        public void Accepts_valid_mac()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateMacList("[\"aa:bb:cc:dd:ee:ff\"]", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Accepts_uppercase_mac()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateMacList("[\"AA:BB:CC:DD:EE:FF\"]", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Rejects_invalid_mac()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateMacList("[\"not-a-mac\"]", errors);
            errors.Should().ContainSingle().Which.Should().Contain("not valid");
        }

        [Fact]
        public void Rejects_mac_with_dashes()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidateMacList("[\"aa-bb-cc-dd-ee-ff\"]", errors);
            errors.Should().ContainSingle().Which.Should().Contain("not valid");
        }
    }

    public class ValidatePortListTests
    {
        [Fact]
        public void Accepts_single_port()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidatePortList("[\"443\"]", "Port", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Accepts_port_range()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidatePortList("[\"27015-27030\"]", "Port", errors);
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Rejects_non_numeric_port()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidatePortList("[\"http\"]", "Port", errors);
            errors.Should().ContainSingle().Which.Should().Contain("not valid");
        }

        [Fact]
        public void Rejects_port_above_65535()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidatePortList("[\"70000\"]", "Port", errors);
            errors.Should().ContainSingle().Which.Should().Contain("out of range");
        }

        [Fact]
        public void Rejects_port_zero()
        {
            var errors = new List<string>();
            WanSteerValidation.ValidatePortList("[\"0\"]", "Port", errors);
            errors.Should().ContainSingle().Which.Should().Contain("out of range");
        }
    }

    public class ValidateRuleTests
    {
        private static WanSteerTrafficClass MakeValidRule() => new()
        {
            Name = "Test Rule",
            TargetWanKey = "WAN2",
            Probability = 1.0,
            DstCidrsJson = "[\"10.0.0.0/8\"]"
        };

        [Fact]
        public void Accepts_valid_rule()
        {
            var errors = WanSteerValidation.ValidateRule(MakeValidRule());
            errors.Should().BeEmpty();
        }

        [Fact]
        public void Requires_name()
        {
            var rule = MakeValidRule();
            rule.Name = "";
            WanSteerValidation.ValidateRule(rule).Should().Contain("Name is required.");
        }

        [Fact]
        public void Requires_target_wan()
        {
            var rule = MakeValidRule();
            rule.TargetWanKey = "";
            WanSteerValidation.ValidateRule(rule).Should().Contain("Target WAN is required.");
        }

        [Fact]
        public void Rejects_zero_probability()
        {
            var rule = MakeValidRule();
            rule.Probability = 0;
            WanSteerValidation.ValidateRule(rule).Should().Contain("Probability must be between 1 and 100%.");
        }

        [Fact]
        public void Rejects_probability_over_one()
        {
            var rule = MakeValidRule();
            rule.Probability = 1.5;
            WanSteerValidation.ValidateRule(rule).Should().Contain("Probability must be between 1 and 100%.");
        }

        [Fact]
        public void Requires_at_least_one_match_criterion()
        {
            var rule = new WanSteerTrafficClass
            {
                Name = "Empty Rule",
                TargetWanKey = "WAN",
                Probability = 1.0
            };
            WanSteerValidation.ValidateRule(rule).Should().Contain(e => e.Contains("At least one match criterion"));
        }

        [Fact]
        public void Protocol_only_is_valid_match_criterion()
        {
            var rule = new WanSteerTrafficClass
            {
                Name = "Protocol Only",
                TargetWanKey = "WAN",
                Probability = 1.0,
                Protocol = "tcp"
            };
            WanSteerValidation.ValidateRule(rule).Should().BeEmpty();
        }

        [Fact]
        public void Ports_without_protocol_errors()
        {
            var rule = new WanSteerTrafficClass
            {
                Name = "Ports No Proto",
                TargetWanKey = "WAN",
                Probability = 1.0,
                DstPortsJson = "[\"443\"]"
            };
            WanSteerValidation.ValidateRule(rule).Should().Contain("Protocol (TCP or UDP) is required when ports are specified.");
        }

        [Fact]
        public void Mac_only_source_is_valid()
        {
            var rule = new WanSteerTrafficClass
            {
                Name = "MAC Rule",
                TargetWanKey = "WAN2",
                Probability = 0.5,
                SrcMacsJson = "[\"aa:bb:cc:dd:ee:ff\"]"
            };
            WanSteerValidation.ValidateRule(rule).Should().BeEmpty();
        }
    }

    public class ToJsonArrayNormalizeCidrsTests
    {
        [Fact]
        public void Appends_slash32_to_bare_ips()
        {
            var result = WanSteerValidation.ToJsonArrayNormalizeCidrs("1.2.3.4");
            result.Should().Be("[\"1.2.3.4/32\"]");
        }

        [Fact]
        public void Preserves_existing_cidrs()
        {
            var result = WanSteerValidation.ToJsonArrayNormalizeCidrs("10.0.0.0/8");
            result.Should().Be("[\"10.0.0.0/8\"]");
        }

        [Fact]
        public void Preserves_ip_ranges()
        {
            var result = WanSteerValidation.ToJsonArrayNormalizeCidrs("192.168.1.1-192.168.1.50");
            result.Should().Be("[\"192.168.1.1-192.168.1.50\"]");
        }

        [Fact]
        public void Handles_multiline_input()
        {
            var result = WanSteerValidation.ToJsonArrayNormalizeCidrs("1.2.3.4\n10.0.0.0/8\n5.6.7.8-5.6.7.9");
            result.Should().Be("[\"1.2.3.4/32\",\"10.0.0.0/8\",\"5.6.7.8-5.6.7.9\"]");
        }

        [Fact]
        public void Returns_null_for_empty_input()
        {
            WanSteerValidation.ToJsonArrayNormalizeCidrs("").Should().BeNull();
            WanSteerValidation.ToJsonArrayNormalizeCidrs(null).Should().BeNull();
            WanSteerValidation.ToJsonArrayNormalizeCidrs("   ").Should().BeNull();
        }

        [Fact]
        public void Trims_whitespace_and_skips_blank_lines()
        {
            var result = WanSteerValidation.ToJsonArrayNormalizeCidrs("  1.2.3.4  \n\n  10.0.0.0/8  \n");
            result.Should().Be("[\"1.2.3.4/32\",\"10.0.0.0/8\"]");
        }
    }
}
