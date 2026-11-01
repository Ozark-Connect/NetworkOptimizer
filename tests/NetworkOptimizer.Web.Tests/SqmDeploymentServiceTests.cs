using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SqmDeploymentServiceTests
{
    private const string NetworkConf = """
        {"meta":{"rc":"ok"},"data":[
          {"_id":"lan1","purpose":"corporate","name":"LAN","wan_networkgroup":"WAN"},
          {"_id":"wan1","purpose":"wan","name":"Fiber","wan_networkgroup":"WAN","wan_smartq_enabled":true,
           "wan_smartq_down_rate":1077000,"wan_smartq_up_rate":999000,"wan_vlan":201,"x_unknown":{"keep":[1,2]}},
          {"_id":"wan2","purpose":"wan","name":"Cable","wan_networkgroup":"WAN2","wan_smartq_enabled":true,
           "wan_smartq_down_rate":"274000","wan_smartq_up_rate":29000},
          {"_id":"wan3","purpose":"wan","name":"Cellular","wan_networkgroup":"WAN3","wan_smartq_enabled":false}
        ]}
        """;

    [Fact]
    public void SmartQueuesNudge_PicksTheWanForItsNetworkGroup_AndKeepsEveryOtherField()
    {
        var nudge = SqmDeploymentService.BuildSmartQueuesNudge(NetworkConf, "WAN");

        nudge.Error.Should().BeNull();
        nudge.Id.Should().Be("wan1", "a LAN network carrying the same group is not a WAN");
        nudge.FromKbps.Should().Be(1077000);
        nudge.ToKbps.Should().Be(1078000);
        var body = System.Text.Json.Nodes.JsonNode.Parse(nudge.Body!)!;
        ((int)body["wan_smartq_down_rate"]!).Should().Be(1078000);
        ((int)body["wan_smartq_up_rate"]!).Should().Be(999000);
        ((int)body["wan_vlan"]!).Should().Be(201);
        body["x_unknown"]!["keep"]!.AsArray().Count.Should().Be(2, "fields the typed model lacks go back unchanged");
    }

    [Fact]
    public void SmartQueuesNudge_ReadsARateSentAsAString()
    {
        var nudge = SqmDeploymentService.BuildSmartQueuesNudge(NetworkConf, "WAN2");

        nudge.Id.Should().Be("wan2");
        nudge.ToKbps.Should().Be(273000);
    }

    [Theory]
    [InlineData("WAN3", "Smart Queues is off")]
    [InlineData("WAN4", "no WAN network")]
    public void SmartQueuesNudge_RefusesAWanItCannotNudge(string group, string error)
    {
        SqmDeploymentService.BuildSmartQueuesNudge(NetworkConf, group).Error.Should().Contain(error);
    }

    [Fact]
    public void SmartQueuesNudge_RefusesAnAmbiguousNetworkGroup()
    {
        var twice = NetworkConf.Replace("\"wan_networkgroup\":\"WAN2\"", "\"wan_networkgroup\":\"WAN\"");

        SqmDeploymentService.BuildSmartQueuesNudge(twice, "WAN").Error.Should().Contain("more than one");
    }

    [Theory]
    [InlineData(1077000, 1078000)]
    [InlineData(1078000, 1077000)]
    [InlineData(274500, 273500)]
    public void NudgedDownRate_AlternatesBetweenTwoValues(int from, int to)
    {
        SqmDeploymentService.NudgedDownRateKbps(from).Should().Be(to);
        SqmDeploymentService.NudgedDownRateKbps(to).Should().Be(from);
    }

    [Fact]
    public void RemoveCakeRoots_DeletesOnlyAdaptiveSqmsOwnCake_AndKeepsTheCakeWans()
    {
        var bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        if (bash == null) return;

        var dir = Path.Combine(Path.GetTempPath(), $"sqm-cake-rm-{Guid.NewGuid():N}");
        foreach (var ifb in new[] { "ifbppp0", "ifbeth4", "ifbeth5", "ifbeth6" })
            Directory.CreateDirectory(Path.Combine(dir, ifb));
        try
        {
            // ifbppp0 keeps its CAKE (deploying with it), ifbeth4 has ours and loses it,
            // ifbeth5 has a cake someone else installed, ifbeth6 is UniFi's htb.
            var command = SqmDeploymentService.BuildRemoveCakeRootsCommand(new[] { "ppp0", "bad name;" })
                .Replace("/sys/class/net", dir);
            var script = @"
pgrep() { return 1; }
tc() {
    case ""$1 $2 $4"" in
        ""qdisc show ifbppp0""|""qdisc show ifbeth4"") echo ""qdisc cake ca4e: root refcnt 2 bandwidth 900Mbit"" ;;
        ""qdisc show ifbeth5"") echo ""qdisc cake 8001: root refcnt 2 bandwidth 900Mbit"" ;;
        ""qdisc show ifbeth6"") echo ""qdisc htb 1: root refcnt 2"" ;;
        *) echo ""TC $*"" ;;
    esac
}
" + command + "\n";

            var psi = new ProcessStartInfo(bash) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(script);
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);

            output.Should().Contain("TC qdisc del dev ifbeth4 root handle ca4e: cake");
            output.Should().NotContain("del dev ifbppp0").And.NotContain("del dev ifbeth5").And.NotContain("del dev ifbeth6");
            SqmDeploymentService.ParseRemovedCakeRoots(output).Should().Equal("ifbeth4");
            command.Should().NotContain("bad name", "only validated interface names reach the command line");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

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
