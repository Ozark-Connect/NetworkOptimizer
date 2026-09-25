using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SqmDeploymentServiceTests
{
    [Fact]
    public void DeploymentStatus_ValidatesTheManagedOoklaBuildAndBothParserDependencies()
    {
        var command = SqmDeploymentService.BuildDeploymentStatusCommand();

        command.Should().Contain(ScriptGenerator.ManagedSpeedtestPath);
        command.Should().Contain(ScriptGenerator.ManagedSpeedtestCliVersion);
        command.Should().Contain(ScriptGenerator.ManagedSpeedtestAarch64BinarySha256);
        command.Should().Contain(ScriptGenerator.ManagedSpeedtestArmhfBinarySha256);
        command.Should().Contain(ScriptGenerator.ManagedSpeedtestX86_64BinarySha256);
        command.Should().Contain("sha256sum -c -");
        command.Should().Contain("---BC_CHECK---");
        command.Should().Contain("---JQ_CHECK---");
        command.Should().NotContain("which speedtest");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentStatus_CountsLegacyBcAndMissingPingGuards(bool legacy)
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"sqm-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "wan-ping.sh"), legacy ? "echo 1 | bc\n" : "PROBE_LOCK=lock\n");
            File.WriteAllText(Path.Combine(directory, "wan-speedtest.sh"), legacy ? "echo 1 | bc\n" : "awk 'BEGIN { print 1 }'\n");
            var command = SqmDeploymentService.BuildDeploymentStatusCommand();
            // Run the two migration probes against fixtures, without gateway commands.
            command = command[command.IndexOf("echo '---SCRIPTS_ON_BC---'", StringComparison.Ordinal)..]
                .Replace("/data/sqm", "'" + directory.Replace("'", "'\\''") + "'");
            using var process = Process.Start(new ProcessStartInfo("/bin/bash")
            {
                ArgumentList = { "-c", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000).Should().BeTrue();
            process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
            var values = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            values.Should().Equal("---SCRIPTS_ON_BC---", legacy ? "2" : "0",
                "---PING_GUARD_MISSING---", legacy ? "1" : "0");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

}
