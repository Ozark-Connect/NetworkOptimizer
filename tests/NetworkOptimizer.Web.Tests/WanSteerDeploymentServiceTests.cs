using System.IO;
using System.Runtime.CompilerServices;
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
            var output = "32508:	from all fwmark 0x1a0000/0x7e0000 lookup 201.eth4.100\n";

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().ContainKey("eth4.100");
            result["eth4.100"].FWMark.Should().Be("0x1a0000");
            result["eth4.100"].RouteTable.Should().Be("201.eth4.100");
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
                "32508:	from all fwmark 0x1a0000/0x7e0000 lookup 201.eth4.100",
                "32510:	from all fwmark 0x6e0000/0x7e0000 lookup 180.gre1",
                "32766:	from all lookup main",
                "32767:	from all lookup default");

            var result = WanSteerDeploymentService.ParseIpRules(output);

            result.Should().HaveCount(4);
            result.Should().ContainKey("eth0");
            result.Should().ContainKey("eth1");
            result.Should().ContainKey("eth4.100");
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

    // The app ships binary contract version 1 (src/wansteer/binary-version, embedded in the
    // assembly). These tests pin the advisory "redeploy" logic against that baseline.
    public class IsBinaryOutdatedTests
    {
        [Fact]
        public void No_warning_when_nothing_is_deployed()
        {
            // First-time deploy: there is no binary yet. Never nag, never block.
            var status = new WanSteerStatus { BinaryDeployed = false, DeployedBinaryVersion = null };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeFalse();
        }

        [Fact]
        public void No_warning_for_null_status()
        {
            WanSteerDeploymentService.IsBinaryOutdated(null).Should().BeFalse();
        }

        [Fact]
        public void No_warning_when_deployed_contract_version_matches()
        {
            var status = new WanSteerStatus { BinaryDeployed = true, DeployedBinaryVersion = 1 };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeFalse();
        }

        [Fact]
        public void Warns_when_deployed_contract_version_is_older()
        {
            var status = new WanSteerStatus { BinaryDeployed = true, DeployedBinaryVersion = 0 };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeTrue();
        }

        [Fact]
        public void No_warning_when_deployed_contract_version_is_newer()
        {
            // Downgraded app vs a newer gateway binary: never nag the user to deploy an older daemon.
            var status = new WanSteerStatus { BinaryDeployed = true, DeployedBinaryVersion = 2 };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeFalse();
        }

        [Theory]
        [InlineData("1.14.7")]
        [InlineData("v1.14.7")]
        [InlineData("1.23.0")]
        [InlineData("1.23.0-alpha.0.2+abc123")]
        public void No_warning_for_preflag_binary_at_or_above_floor(string releaseVersion)
        {
            // Old binary without the -binary-version flag, but its release is >= v1.14.7 (the floor
            // where the current daemon first shipped). It already runs the current daemon.
            var status = new WanSteerStatus
            {
                BinaryDeployed = true,
                DeployedBinaryVersion = null,
                Version = releaseVersion
            };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeFalse();
        }

        [Theory]
        [InlineData("1.14.6")]
        [InlineData("1.0.0")]
        [InlineData("0.9.5")]
        public void Warns_for_preflag_binary_below_floor(string releaseVersion)
        {
            var status = new WanSteerStatus
            {
                BinaryDeployed = true,
                DeployedBinaryVersion = null,
                Version = releaseVersion
            };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeTrue();
        }

        [Theory]
        [InlineData("dev")]
        [InlineData(null)]
        [InlineData("")]
        public void Warns_for_deployed_binary_that_cannot_identify_itself(string? releaseVersion)
        {
            // A binary is present but reports neither a contract version nor a parseable release
            // (a "dev"/source build that predates the -binary-version flag). It can't prove it is
            // current, so we flag it. This is resolvable now: redeploying pushes a binary that
            // reports the contract version (unlike the unresolvable loop in #898). The warning is
            // advisory only and never blocks deploying.
            var status = new WanSteerStatus
            {
                BinaryDeployed = true,
                DeployedBinaryVersion = null,
                Version = releaseVersion
            };

            WanSteerDeploymentService.IsBinaryOutdated(status).Should().BeTrue();
        }
    }

    public class EmbeddedBinaryVersionTests
    {
        // Guard against a silent failure mode: the app reads its expected contract version from the
        // embedded src/wansteer/binary-version (the same file the Go binary embeds). If that
        // EmbeddedResource wiring ever breaks - a .csproj refactor, rename, or path change - the
        // read silently falls back to a default and the redeploy prompt stops working after a future
        // contract bump, with no other signal. This test fails loudly instead: it verifies the
        // resource resolves and that the app's expected version matches the source file on disk.
        [Fact]
        public void Expected_version_resolves_from_embedded_resource_and_matches_source()
        {
            var asm = typeof(WanSteerDeploymentService).Assembly;
            using var stream = asm.GetManifestResourceStream("wansteer.binary-version");
            stream.Should().NotBeNull(
                "the binary-version EmbeddedResource must be wired in NetworkOptimizer.Web.csproj");

            using var reader = new StreamReader(stream!);
            var embedded = int.Parse(reader.ReadToEnd().Trim());

            var repoFile = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(SourceFilePath())!, "..", "..", "src", "wansteer", "binary-version"));
            File.Exists(repoFile).Should().BeTrue($"expected source of truth at {repoFile}");
            var onDisk = int.Parse(File.ReadAllText(repoFile).Trim());

            embedded.Should().Be(onDisk, "the embedded resource must match the source file");
            WanSteerDeploymentService.ExpectedBinaryVersion.Should().Be(onDisk,
                "the app's expected contract version must come from src/wansteer/binary-version, not a silent fallback");
        }

        private static string SourceFilePath([CallerFilePath] string path = "") => path;
    }
}
