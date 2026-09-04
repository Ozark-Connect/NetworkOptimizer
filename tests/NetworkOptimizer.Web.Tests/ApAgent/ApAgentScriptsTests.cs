using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The architecture gate, the status probe's parser, and the shell the server writes onto an access
/// point. All of it runs on hardware nobody here owns, so the exact text is pinned.
/// </summary>
public class ApAgentScriptsTests
{
    private const string ProbeOutput = """
        ---ARCH---
        armv7l
        ---MODEL---
        U7-Pro-XGS
        ---FIRMWARE---
        8.7.11.19419
        ---PROCD---
        present
        ---BINARY---
        exists
        ---WRAPPER---
        exists
        ---PROCESS---
        running
        ---VERSION---
        2.7.1
        ---BINARY_VERSION---
        1
        ---MD5---
        3366043c8f699cf4aabbccddeeff0011
        ---SFTP---
        present
        ---SCP---
        present
        """;

    [Theory]
    [InlineData("armv7l", true)]
    [InlineData("armv6l", true)]
    [InlineData("armv8l", true)]
    [InlineData("aarch64", false)]
    [InlineData("mips", false)]
    [InlineData("x86_64", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyThirtyTwoBitArm_has_a_build(string? machine, bool supported)
    {
        ApAgentScripts.SupportsArchitecture(machine).Should().Be(supported);
    }

    [Fact]
    public void AnUnsupportedArchitecture_says_what_it_is_rather_than_failing_cryptically()
    {
        ApAgentScripts.UnsupportedReason("aarch64").Should().Contain("aarch64").And.Contain("armv7l");
    }

    [Fact]
    public void TheStatusProbe_reads_every_field_in_one_round_trip()
    {
        var status = ApAgentScripts.ParseStatus(ProbeOutput, success: true);

        status.Reachable.Should().BeTrue();
        status.Machine.Should().Be("armv7l");
        status.Model.Should().Be("U7-Pro-XGS");
        status.Firmware.Should().Be("8.7.11.19419");
        status.SupportedArchitecture.Should().BeTrue();
        status.ProcdAvailable.Should().BeTrue();
        status.BinaryDeployed.Should().BeTrue();
        status.WrapperDeployed.Should().BeTrue();
        status.IsRunning.Should().BeTrue();
        status.Version.Should().Be("2.7.1");
        status.DeployedBinaryVersion.Should().Be(1);
        status.BinaryMd5.Should().Be("3366043c8f699cf4aabbccddeeff0011");
        status.SftpAvailable.Should().BeTrue();
        status.ScpAvailable.Should().BeTrue();
    }

    [Fact]
    public void AnAccessPointWithNothingDeployed_reports_absence_rather_than_nulls()
    {
        var output = """
            ---ARCH---
            armv7l
            ---MODEL---
            ---FIRMWARE---
            ---PROCD---
            absent
            ---BINARY---
            missing
            ---WRAPPER---
            missing
            ---PROCESS---
            stopped
            ---VERSION---
            ---BINARY_VERSION---
            ---MD5---
            ---SFTP---
            absent
            ---SCP---
            absent
            """;

        var status = ApAgentScripts.ParseStatus(output, success: true);

        status.BinaryDeployed.Should().BeFalse();
        status.IsRunning.Should().BeFalse();
        status.ProcdAvailable.Should().BeFalse();
        status.DeployedBinaryVersion.Should().BeNull();
        status.BinaryMd5.Should().BeNull();
        status.SupportedArchitecture.Should().BeTrue();
        status.SftpAvailable.Should().BeFalse();
        status.ScpAvailable.Should().BeFalse();
    }

    [Fact]
    public void AProbeMissingTheCapabilitySections_reads_both_transfers_as_absent()
    {
        var status = ApAgentScripts.ParseStatus("---ARCH---\narmv7l", success: true);

        status.SftpAvailable.Should().BeFalse();
        status.ScpAvailable.Should().BeFalse();
    }

    [Fact]
    public void AFailedProbe_carries_the_error_and_trusts_nothing_else()
    {
        var status = ApAgentScripts.ParseStatus("Connection failed: refused", success: false);

        status.Reachable.Should().BeFalse();
        status.Error.Should().Be("Connection failed: refused");
        status.BinaryDeployed.Should().BeFalse();
        status.SupportedArchitecture.Should().BeFalse();
    }

    [Fact]
    public void TheProbeCommand_asks_for_every_section_the_parser_reads()
    {
        var command = ApAgentScripts.StatusProbeCommand();

        foreach (var section in new[] { "ARCH", "MODEL", "FIRMWARE", "PROCD", "BINARY", "WRAPPER", "PROCESS", "VERSION", "BINARY_VERSION", "MD5", "SFTP", "SCP" })
            command.Should().Contain($"---{section}---");
    }

    [Fact]
    public void TheCapabilityProbes_pin_the_braceless_shell_form_measured_on_busybox_ash()
    {
        // At equal precedence "a || b && c || d" parses as ((a || b) && c) || d, which is what we
        // want. Do not "tidy" this with -o (POSIX-obsolescent) or braces.
        var command = ApAgentScripts.StatusProbeCommand();

        command.Should().Contain("test -f /usr/lib/sftp-server || test -f /usr/libexec/sftp-server && echo present || echo absent");
        command.Should().Contain("test -x /usr/sbin/scp || test -x /usr/bin/scp && echo present || echo absent");
    }

    [Fact]
    public void TheProcdDefinition_passes_the_token_in_the_environment_never_on_the_command_line()
    {
        var script = ApAgentScripts.InitScript("0123456789abcdef0123456789abcdef");

        script.Should().Contain("procd_set_param env APAGENT_TOKEN='0123456789abcdef0123456789abcdef'");
        script.Should().NotContain("-token ");
        script.Should().Contain("USE_PROCD=1");
    }

    [Fact]
    public void TheProcdDefinition_starts_the_wrapper_so_the_architecture_gate_still_applies()
    {
        ApAgentScripts.InitScript("0123456789abcdef0123456789abcdef")
            .Should().Contain($"procd_set_param command {ApAgentPaths.RemoteWrapperPath}");
    }

    [Fact]
    public void WithoutProcd_the_token_goes_through_a_file_rather_than_the_command_line()
    {
        var command = ApAgentScripts.StartCommand(procdAvailable: false);

        command.Should().Contain($"-token-file {ApAgentPaths.RemoteTokenPath}");
        command.Should().NotContain("-token ");
    }

    [Fact]
    public void RemovingTheAgent_clears_the_install_directory_and_the_service_definition()
    {
        var command = ApAgentScripts.RemoveCommand(procdAvailable: true);

        command.Should().Contain($"rm -rf {ApAgentPaths.RemoteDir}");
        command.Should().Contain($"rm -f {ApAgentPaths.RemoteInitScriptPath}");
    }

    [Fact]
    public void EverythingTheAgentTouches_lives_in_tmpfs_or_etc_which_is_also_tmpfs()
    {
        // The config partition behind /etc/persistent is 1 MB, so a Go binary provably cannot live
        // there. Nothing may be written to it.
        ApAgentPaths.RemoteDir.Should().StartWith("/tmp/");
        ApAgentPaths.RemoteBinaryPath.Should().NotContain("/etc/persistent");
        ApAgentPaths.RemoteInitScriptPath.Should().NotContain("/etc/persistent");
    }

    [Fact]
    public void AWrittenFile_is_base64_piped_so_content_never_needs_shell_escaping()
    {
        var command = ApAgentScripts.WriteFileCommand("hello\nworld\n", "/tmp/x", "600");

        command.Should().Contain("base64 -d > /tmp/x");
        command.Should().Contain("chmod 600 /tmp/x");
        command.Should().Contain(Convert.ToBase64String("hello\nworld\n"u8.ToArray()));
    }

    [Fact]
    public void AQuoteInAValue_cannot_break_out_of_the_shell_quoting()
    {
        ApAgentScripts.ShellQuote("a'b").Should().Be(@"'a'\''b'");
    }
}
