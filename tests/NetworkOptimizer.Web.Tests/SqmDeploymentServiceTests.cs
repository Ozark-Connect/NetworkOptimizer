using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SqmDeploymentServiceTests
{
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

    /// <summary>
    /// Runs the restore command against stubs: <paramref name="udapi"/> is the body of a
    /// ubios-udapi-client function (its POST counts rebuilds), <paramref name="htbAfter"/> whether
    /// class 1:1 shows after a rebuild, and <paramref name="redirectBackOnRebuild"/> the rebuild after
    /// which eth4's ingress redirect is back (1 = the first). ifbeth4 carries UniFi's htb, ifbeth6
    /// Adaptive SQM's cake, ifb0 no tree at all; eth4 redirects to ifbeth4.
    /// </summary>
    private static (string Output, string Calls)? RunRestore(string udapi, bool htbAfter = true, int redirectBackOnRebuild = 1)
    {
        var bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows() || bash == null) return null;

        var dir = Path.Combine(Path.GetTempPath(), $"sqm-restore-{Guid.NewGuid():N}");
        foreach (var ifb in new[] { "ifb0", "ifbeth4", "ifbeth6" })
            Directory.CreateDirectory(Path.Combine(dir, "net", ifb));
        try
        {
            var command = SqmDeploymentService.BuildRestoreUniFiQueuesCommand()
                .Replace("/sys/class/net", Path.Combine(dir, "net"))
                .Replace("/tmp/netopt-restore.json", Path.Combine(dir, "q.json"));
            var calls = Path.Combine(dir, "calls");
            var script = $@"
CALLS='{calls}'
REBUILDS='{Path.Combine(dir, "rebuilds")}'
echo 0 > ""$REBUILDS""
sleep() {{ :; }}
ubios-udapi-client() {{
    echo ""UDAPI $*"" >> ""$CALLS""
    [ ""$2 $3"" = ""POST /qos/fw/queues"" ] && echo $(( $(cat ""$REBUILDS"") + 1 )) > ""$REBUILDS""
    {udapi}
}}
tc() {{
    local n=$(cat ""$REBUILDS"")
    case ""$1 $2 $4"" in
        ""qdisc show "") echo ""qdisc ingress ffff: dev eth4 parent ffff:fff1 ----------------"" ;;
        ""qdisc show ifbeth4"") echo ""qdisc htb 1: root refcnt 2 r2q 10 default 0x2"" ;;
        ""qdisc show ifbeth6"") echo ""qdisc cake ca4e: root refcnt 2 bandwidth 900Mbit"" ;;
        ""class show ifbeth4""|""class show ifbeth6"") {(htbAfter ? "echo \"class htb 1:1 root rate 1078Mbit ceil 1078Mbit\"" : ":")} ;;
        ""filter show eth4"") if [ ""$n"" -eq 0 ] || [ ""$n"" -ge {redirectBackOnRebuild} ]; then
            echo ""filter parent ffff: protocol all pref 49152 u32 chain 0 action order 1: mirred (Egress Redirect to device ifbeth4) stolen""; fi ;;
    esac
}}
" + command + "\n";

            var psi = new ProcessStartInfo(bash) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            // The stubs come from this file's literals, which carry CRLF on a Windows checkout.
            proc.StandardInput.Write(script.Replace("\r\n", "\n"));
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            return (output, File.Exists(calls) ? File.ReadAllText(calls) : "");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The udapi stub's answers, as ubios-udapi-client -r prints them.
    private const string UdapiAnswers = """
        case "$2 $3" in
            "GET /qos/global") printf '{\n "enabled": true\n}\n' ;;
            "POST /qos/fw/queues") printf '[\n {\n  "name": "netopt_rebuild"\n }\n]\n' ;;
            "DELETE /qos/fw/queues/netopt_rebuild") printf '{\n "statusCode": 200\n}\n' ;;
        esac
        """;

    private static int Posts(string calls) => calls.Split('\n').Count(c => c.Contains("POST"));

    [Fact]
    public void RestoreUniFiQueues_AddsAndDeletesATemporaryQueue_AndChecksEveryShapedIfb()
    {
        if (RunRestore(UdapiAnswers) is not { } run) return;
        var (output, calls) = run;

        output.Should().Contain("RESTORE_OK");
        SqmDeploymentService.ParseRestoreUniFiQueues(output).Should().BeNull();
        var lines = calls.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(4);
        lines[0].Should().Be("UDAPI -r GET /qos/global");
        lines[1].Should().Be("UDAPI -r DELETE /qos/fw/queues/netopt_rebuild", "a queue left by an interrupted run goes first");
        lines[2].Should().StartWith("UDAPI -r POST /qos/fw/queues @");
        lines[3].Should().Be("UDAPI -r DELETE /qos/fw/queues/netopt_rebuild");
    }

    [Fact]
    public void RestoreUniFiQueues_RebuildsOnceMore_WhenAWansRedirectDidNotComeBack()
    {
        if (RunRestore(UdapiAnswers, redirectBackOnRebuild: 2) is not { } run) return;

        run.Output.Should().Contain("RESTORE_OK");
        Posts(run.Calls).Should().Be(2);
    }

    [Fact]
    public void RestoreUniFiQueues_ReportsARedirectStillMissing_AfterTheSecondRebuild()
    {
        if (RunRestore(UdapiAnswers, redirectBackOnRebuild: 99) is not { } run) return;

        run.Output.Should().Contain("RESTORE_MISSING eth4(redirect)");
        Posts(run.Calls).Should().Be(2, "one retry, no more");
        SqmDeploymentService.ParseRestoreUniFiQueues(run.Output).Should().Contain("eth4(redirect)");
    }

    [Fact]
    public void RestoreUniFiQueues_NamesTheIfbsWhoseTreeDidNotComeBack()
    {
        if (RunRestore(UdapiAnswers, htbAfter: false) is not { } run) return;
        var output = run.Output;

        output.Should().Contain("RESTORE_MISSING ifbeth4 ifbeth6", "ifb0 carried no tree before, so it is not expected to");
        SqmDeploymentService.ParseRestoreUniFiQueues(output).Should().Contain("ifbeth4, ifbeth6");
        Posts(run.Calls).Should().Be(2);
    }

    [Fact]
    public void RestoreUniFiQueues_LeavesAGatewayWithQosOffAlone()
    {
        if (RunRestore("""case "$2 $3" in "GET /qos/global") printf '{\n "enabled": false\n}\n' ;; esac""") is not { } run) return;
        var (output, calls) = run;

        output.Should().Contain("RESTORE_SKIPPED");
        SqmDeploymentService.ParseRestoreUniFiQueues(output).Should().BeNull();
        calls.Should().NotContain("POST");
    }

    [Fact]
    public void RestoreUniFiQueues_StopsWhenThePostIsRefused()
    {
        var refused = UdapiAnswers.Replace("""printf '[\n {\n  "name": "netopt_rebuild"\n }\n]\n'""",
            """printf '{\n "message": "json object must be an array",\n "statusCode": 400\n}\n'""");
        if (RunRestore(refused) is not { } run) return;
        var (output, calls) = run;

        output.Should().Contain("RESTORE_FAILED post");
        calls.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(c => c.Contains("DELETE")).Should().Be(1,
            "only the clean-up of a leftover queue runs; nothing was added to delete");
    }

    [Theory]
    [InlineData("RESTORE_OK", null)]
    [InlineData("RESTORE_SKIPPED qos-off", null)]
    [InlineData("noise\nRESTORE_FAILED delete\n", "netopt_rebuild could not be deleted")]
    [InlineData("RESTORE_FAILED no-udapi", "no UniFi QoS service")]
    [InlineData("RESTORE_FAILED qos-read", "could not be read")]
    [InlineData("RESTORE_MISSING ifbeth4", "ifbeth4")]
    [InlineData("", "no result")]
    public void ParseRestoreUniFiQueues_ReadsTheLastResultLine(string output, string? error)
    {
        var parsed = SqmDeploymentService.ParseRestoreUniFiQueues(output);
        if (error == null) parsed.Should().BeNull();
        else parsed.Should().Contain(error);
    }

    [Fact]
    public void WaitForSqmRuns_CoversPingCalibrationAndALearningProbe_AndTheRestoreNoLongerWaits()
    {
        var wait = SqmDeploymentService.BuildWaitForSqmRunsCommand();

        wait.Should().Contain("pgrep -f -- '[-](ping|speedtest)\\.sh'");
        wait.Should().Contain("/data/sqm/probe-*.lock");
        wait.Should().Contain($"-lt {ScriptGenerator.ProbeLockMaxAgeSeconds} ]", "a lock left by a killed probe does not hold it up");
        wait.Should().NotContain("\r");
        SqmDeploymentService.BuildRestoreUniFiQueuesCommand().Should().NotContain("pgrep");
        SqmDeploymentService.BuildRemoveCakeRootsCommand(Array.Empty<string>()).Should().NotContain("pgrep");
    }

    [Fact]
    public void ReadExistingSqm_ReadsEachPingScriptsInterfaceQdiscAndTreeModel()
    {
        var bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows() || bash == null) return;

        var dir = Path.Combine(Path.GetTempPath(), $"sqm-existing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sqm"));
        Directory.CreateDirectory(Path.Combine(dir, "net", "ifbeth9"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "sqm", "fiber-ping.sh"),
                $"INTERFACE=\"eth4\"\nDOWNLOAD_QDISC=\"cake\"\nTREE_MODEL={ScriptGenerator.TreeModel}\n");
            File.WriteAllText(Path.Combine(dir, "sqm", "cable-ping.sh"), "INTERFACE=\"eth5\"\n");
            var command = SqmDeploymentService.BuildReadExistingSqmCommand()
                .Replace("/data/sqm", Path.Combine(dir, "sqm"))
                .Replace("/sys/class/net", Path.Combine(dir, "net"));
            var script = "tc() { [ \"$4\" = ifbeth9 ] && echo 'class htb 1:2 parent 1:1 leaf 2: prio 7 rate 100Kbit ceil 1Gbit'; }\n" + command + "\n";

            var psi = new ProcessStartInfo(bash) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(script.Replace("\r\n", "\n"));
            proc.StandardInput.Close();
            var existing = SqmDeploymentService.ParseExistingSqm(proc.StandardOutput.ReadToEnd());
            proc.WaitForExit(30_000);

            existing.Scripts.Should().BeEquivalentTo(new[]
            {
                new SqmDeploymentService.ExistingSqmScript("eth4", "cake", ScriptGenerator.TreeModel),
                new SqmDeploymentService.ExistingSqmScript("eth5", "", 0),
            });
            existing.TunedIfbs.Should().Equal("ifbeth9");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static SqmDeploymentService.ExistingSqm Existing(params (string Iface, string Qdisc, int Model)[] scripts) =>
        new(scripts.Select(s => new SqmDeploymentService.ExistingSqmScript(s.Iface, s.Qdisc, s.Model)).ToList(), []);

    private static readonly int Current = ScriptGenerator.TreeModel;

    [Fact]
    public void RestoreReason_SkipsARoutineDeploy()
    {
        SqmDeploymentService.RestoreReason(Existing(("eth4", "htb", Current), ("eth5", "cake", Current)),
            ["eth4", "eth5"], ["eth5"], []).Should().BeNull();
        SqmDeploymentService.RestoreReason(Existing(("eth4", "htb", Current)), ["eth4"], ["eth4"], [])
            .Should().BeNull("HTB to CAKE: CAKE replaces the tree anyway");
        SqmDeploymentService.RestoreReason(new SqmDeploymentService.ExistingSqm([], []), ["eth4"], [], [])
            .Should().BeNull("a first deploy over UniFi's own trees");
    }

    [Fact]
    public void RestoreReason_RestoresWhenCakeIsTurnedOff()
    {
        SqmDeploymentService.RestoreReason(Existing(("eth4", "cake", Current)), ["eth4"], [], [])
            .Should().Contain("CAKE turned off on eth4");
        SqmDeploymentService.RestoreReason(Existing(("eth4", "htb", Current)), ["eth4"], [], ["ifbeth4"])
            .Should().Contain("CAKE turned off on ifbeth4", "a CAKE root the removal just took off counts too");
    }

    [Fact]
    public void RestoreReason_RestoresWhenAWanIsNoLongerDeployed()
    {
        SqmDeploymentService.RestoreReason(Existing(("eth4", "htb", Current), ("eth5", "htb", Current)), ["eth4"], [], [])
            .Should().Contain("no longer deployed on eth5");
    }

    [Fact]
    public void RestoreReason_RestoresOnceOverScriptsFromAnEarlierVersion()
    {
        SqmDeploymentService.RestoreReason(Existing(("eth4", "", 0)), ["eth4"], [], [])
            .Should().Contain("earlier version");
        SqmDeploymentService.RestoreReason(new SqmDeploymentService.ExistingSqm([], ["ifbeth4"]), ["eth4"], [], [])
            .Should().Contain("earlier version", "trees an older Remove left tuned, with no scripts to say so");
    }

    [Fact]
    public void RestoreReason_RestoresWithoutADeployList()
    {
        SqmDeploymentService.RestoreReason(Existing(("eth4", "htb", Current)), null, [], []).Should().NotBeNull();
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


    /// <summary>
    /// Runs the restore against an IFB the CAKE root was just deleted from: it shows the kernel's
    /// default fq_codel root, and <paramref name="treeBack"/> says whether UniFi's class 1:1 is there
    /// after the rebuild. eth6 redirects to it throughout.
    /// </summary>
    private static string? RunRestoreAfterCakeRemoval(bool treeBack, IEnumerable<string>? cakeRemovedFrom)
    {
        var bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows() || bash == null) return null;
        var dir = Path.Combine(Path.GetTempPath(), $"sqm-restore-cleared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "net", "ifbeth6"));
        try
        {
            var command = SqmDeploymentService.BuildRestoreUniFiQueuesCommand(cakeRemovedFrom)
                .Replace("/sys/class/net", Path.Combine(dir, "net"))
                .Replace("/tmp/netopt-restore.json", Path.Combine(dir, "q.json"));
            var script = "sleep() { :; }\nubios-udapi-client() {\n" + UdapiAnswers + "\n}\n" + $$"""
                tc() {
                    case "$1 $2 $4" in
                        "qdisc show ") echo "qdisc ingress ffff: dev eth6 parent ffff:fff1 ----------------" ;;
                        "qdisc show ifbeth6") echo "qdisc fq_codel 0: root refcnt 2 limit 10240p" ;;
                        "class show ifbeth6") {{(treeBack ? "echo \"class htb 1:1 root rate 900Mbit ceil 900Mbit\"" : ":")}} ;;
                        "filter show eth6") echo "filter parent ffff: protocol all pref 49152 u32 chain 0 action order 1: mirred (Egress Redirect to device ifbeth6) stolen" ;;
                    esac
                }
                """ + "\n" + command + "\n";
            var psi = new ProcessStartInfo(bash) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(script.Replace("\r\n", "\n"));
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            return output;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RestoreUniFiQueues_ChecksTheIfbsCakeWasJustRemovedFrom()
    {
        if (RunRestoreAfterCakeRemoval(treeBack: false, ["ifbeth6", "bad name;"]) is not { } output) return;

        output.Should().Contain("RESTORE_MISSING ifbeth6", "its tree did not come back, though it only shows the default qdisc");
        SqmDeploymentService.ParseRestoreUniFiQueues(output).Should().Contain("ifbeth6");
        SqmDeploymentService.BuildRestoreUniFiQueuesCommand(["bad name;"]).Should().NotContain("bad name");
    }

    [Fact]
    public void RestoreUniFiQueues_IsOk_WhenTheTreeIsBackOnAnIfbCakeWasRemovedFrom()
    {
        if (RunRestoreAfterCakeRemoval(treeBack: true, ["ifbeth6"]) is not { } output) return;

        output.Should().Contain("RESTORE_OK");
    }

    [Fact]
    public void RestoreUniFiQueues_FailsWhenTheQosStateCannotBeRead()
    {
        if (RunRestore("""case "$2 $3" in "GET /qos/global") echo 'error: connection refused' >&2; return 1 ;; esac""") is not { } run) return;

        run.Output.Should().Contain("RESTORE_FAILED qos-read").And.NotContain("RESTORE_SKIPPED");
        SqmDeploymentService.ParseRestoreUniFiQueues(run.Output).Should().Contain("could not be read");
        run.Calls.Should().NotContain("POST");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void WaitForSqmRuns_SaysWhenNothingIsLeft_AndStaysQuietWhenItRunsOut(bool busy, bool done)
    {
        var bash = new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows() || bash == null) return;

        var script = $"sleep() {{ :; }}\npgrep() {{ return {(busy ? 0 : 1)}; }}\n" + SqmDeploymentService.BuildWaitForSqmRunsCommand() + "\necho \"EXIT=$?\"\n";
        var psi = new ProcessStartInfo(bash) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Write(script.Replace("\r\n", "\n"));
        proc.StandardInput.Close();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30_000);

        output.Contains(SqmDeploymentService.SqmRunsDoneMarker).Should().Be(done);
        output.Should().Contain("EXIT=0");
    }
}
