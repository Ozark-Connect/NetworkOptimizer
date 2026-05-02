using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class WanSteerDeploymentServiceTests
{
    public class ParseIpRulesTests
    {
        [Fact]
        public void Parses_single_eth_interface()
        {
            var output = "32000:	from all fwmark 0x200000/0x7e0000 lookup 201.eth4\n";

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().ContainKey("eth4");
            result["eth4"].FWMark.Should().Be("0x200000");
            result["eth4"].RouteTable.Should().Be("201.eth4");
        }

        [Fact]
        public void Parses_multiple_wan_interfaces()
        {
            var output = string.Join("\n",
                "0:	from all lookup local",
                "32000:	from all fwmark 0x200000/0x7e0000 lookup 201.eth4",
                "32000:	from all fwmark 0x400000/0x7e0000 lookup 202.eth5",
                "32766:	from all lookup main",
                "32767:	from all lookup default");

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().HaveCount(2);
            result.Should().ContainKey("eth4");
            result.Should().ContainKey("eth5");
            result["eth5"].FWMark.Should().Be("0x400000");
            result["eth5"].RouteTable.Should().Be("202.eth5");
        }

        [Fact]
        public void Parses_ppp_interfaces()
        {
            var output = "32000:	from all fwmark 0x200000/0x7e0000 lookup 201.ppp0\n";

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().ContainKey("ppp0");
            result["ppp0"].RouteTable.Should().Be("201.ppp0");
        }

        [Fact]
        public void Returns_empty_for_no_fwmark_rules()
        {
            var output = string.Join("\n",
                "0:	from all lookup local",
                "32766:	from all lookup main",
                "32767:	from all lookup default");

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().BeEmpty();
        }

        [Fact]
        public void Returns_empty_for_empty_output()
        {
            WanSteerDeploymentService.ParseIpRules("").Should().BeEmpty();
        }

        [Fact]
        public void Parses_vlan_tagged_interface()
        {
            var output = "32508:	from all fwmark 0x1a0000/0x7e0000 lookup 201.eth6.228\n";

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().ContainKey("eth6.228");
            result["eth6.228"].FWMark.Should().Be("0x1a0000");
            result["eth6.228"].RouteTable.Should().Be("201.eth6.228");
        }

        [Fact]
        public void Parses_gre_interface()
        {
            var output = "32510:	from all fwmark 0x6e0000/0x7e0000 lookup 180.gre1\n";

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().ContainKey("gre1");
            result["gre1"].FWMark.Should().Be("0x6e0000");
            result["gre1"].RouteTable.Should().Be("180.gre1");
        }

        [Fact]
        public void Parses_real_gateway_output_with_mixed_interfaces()
        {
            var output = string.Join("\n",
                "0:	from all lookup local",
                "32504:	from all fwmark 0x1c0000/0x7e0000 lookup 202.eth0",
                "32506:	from all fwmark 0x720000/0x7e0000 lookup 182.eth1",
                "32508:	from all fwmark 0x1a0000/0x7e0000 lookup 201.eth6.228",
                "32510:	from all fwmark 0x6e0000/0x7e0000 lookup 180.gre1",
                "32766:	from all lookup main",
                "32767:	from all lookup default");

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().HaveCount(4);
            result.Should().ContainKey("eth0");
            result.Should().ContainKey("eth1");
            result.Should().ContainKey("eth6.228");
            result.Should().ContainKey("gre1");
        }

        [Fact]
        public void Ignores_non_matching_fwmark_masks()
        {
            // Different mask than 0x7e0000 - should not match
            var output = "32000:	from all fwmark 0x200000/0xffff00 lookup 201.eth4\n";

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().BeEmpty();
        }
    }

    public class SanitizeWanKeyTests
    {
        [Theory]
        [InlineData("WAN", "wan")]
        [InlineData("WAN 2", "wan-2")]
        [InlineData("My WAN Connection", "my-wan-connection")]
        [InlineData("WAN_Backup", "wan-backup")]
        [InlineData("  spaces  ", "spaces")]
        [InlineData("UPPER CASE!", "upper-case")]
        [InlineData("already-kebab", "already-kebab")]
        [InlineData("Special@#$Characters", "special-characters")]
        public void Converts_to_kebab_case(string input, string expected)
        {
            WanSteerDeploymentService.SanitizeWanKey(input).Should().Be(expected);
        }
    }

    public class SplitCidrsAndRangesTests
    {
        [Fact]
        public void Separates_cidrs_from_ranges()
        {
            var json = "[\"10.0.0.0/8\", \"192.168.1.1-192.168.1.50\", \"172.16.0.0/12\"]";

            var (cidrs, ranges) = WanSteerDeploymentService.SplitCidrsAndRanges(json);

            cidrs.Should().Equal("10.0.0.0/8", "172.16.0.0/12");
            ranges.Should().Equal("192.168.1.1-192.168.1.50");
        }

        [Fact]
        public void All_cidrs_no_ranges()
        {
            var json = "[\"10.0.0.0/8\", \"192.168.1.0/24\"]";

            var (cidrs, ranges) = WanSteerDeploymentService.SplitCidrsAndRanges(json);

            cidrs.Should().HaveCount(2);
            ranges.Should().BeEmpty();
        }

        [Fact]
        public void All_ranges_no_cidrs()
        {
            var json = "[\"10.0.0.1-10.0.0.50\"]";

            var (cidrs, ranges) = WanSteerDeploymentService.SplitCidrsAndRanges(json);

            cidrs.Should().BeEmpty();
            ranges.Should().HaveCount(1);
        }

        [Fact]
        public void Empty_array_returns_empty_lists()
        {
            var json = "[]";

            var (cidrs, ranges) = WanSteerDeploymentService.SplitCidrsAndRanges(json);

            cidrs.Should().BeEmpty();
            ranges.Should().BeEmpty();
        }

        [Fact]
        public void Bare_ip_without_dash_goes_to_cidrs()
        {
            var json = "[\"1.2.3.4/32\"]";

            var (cidrs, ranges) = WanSteerDeploymentService.SplitCidrsAndRanges(json);

            cidrs.Should().Equal("1.2.3.4/32");
            ranges.Should().BeEmpty();
        }
    }

    public class ParseDelimitedOutputTests
    {
        [Fact]
        public void Parses_multiple_sections()
        {
            var output = "---PROCESS---\nrunning\n---STATUS---\n{\"ok\":true}\n---VERSION---\nv1.0.0\n";

            var sections = WanSteerDeploymentService.ParseDelimitedOutput(output);

            sections.Should().ContainKey("PROCESS");
            sections.Should().ContainKey("STATUS");
            sections.Should().ContainKey("VERSION");
            sections["PROCESS"].Should().Contain("running");
            sections["STATUS"].Should().Contain("{\"ok\":true}");
            sections["VERSION"].Should().Contain("v1.0.0");
        }

        [Fact]
        public void Returns_empty_for_no_delimiters()
        {
            var sections = WanSteerDeploymentService.ParseDelimitedOutput("just some text\n");

            sections.Should().BeEmpty();
        }

        [Fact]
        public void Handles_empty_sections()
        {
            var output = "---A---\n---B---\nvalue\n";

            var sections = WanSteerDeploymentService.ParseDelimitedOutput(output);

            sections["A"].Should().BeEmpty();
            sections["B"].Should().Contain("value");
        }
    }

    public class GetSectionTests
    {
        [Fact]
        public void Returns_value_for_existing_key()
        {
            var sections = new Dictionary<string, string> { ["KEY"] = "value" };

            WanSteerDeploymentService.GetSection(sections, "KEY").Should().Be("value");
        }

        [Fact]
        public void Returns_empty_for_missing_key()
        {
            var sections = new Dictionary<string, string>();

            WanSteerDeploymentService.GetSection(sections, "MISSING").Should().BeEmpty();
        }
    }

    public class GenerateBootScriptTests
    {
        [Fact]
        public void Contains_shebang_and_binary_path()
        {
            var script = WanSteerDeploymentService.GenerateBootScript();

            script.Should().StartWith("#!/bin/sh");
            script.Should().Contain("/data/wan-steer/wansteer");
            script.Should().Contain("/data/wan-steer/config.json");
        }

        [Fact]
        public void Includes_sleep_delay_for_unifi_boot()
        {
            var script = WanSteerDeploymentService.GenerateBootScript();

            script.Should().Contain("sleep 30");
        }

        [Fact]
        public void Checks_binary_is_executable()
        {
            var script = WanSteerDeploymentService.GenerateBootScript();

            script.Should().Contain("-x /data/wan-steer/wansteer");
        }
    }
}
