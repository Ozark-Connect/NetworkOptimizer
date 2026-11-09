using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// The ping script's hysteresis: a one-off latency cut recovers on the next normal run; a cut soon
/// after the last recovery (oscillation) holds for <see cref="ScriptGenerator.HoldRuns"/> runs, then
/// the rate climbs one LATENCY_INCREASE step per run instead of jumping back to the ceiling.
/// </summary>
public class PingHysteresisTests
{
    private static string? BashPath() =>
        OperatingSystem.IsWindows() || !File.Exists("/bin/bash") ? null : "/bin/bash";

    /// <summary>
    /// Runs the hysteresis once. <paramref name="prevRate"/> is what the tree carries (empty for no tree),
    /// <paramref name="computed"/> the rate the latency logic produced, <paramref name="hold"/> the
    /// hold file's contents (null for none). Returns the rate it settles on and the new hold count.
    /// </summary>
    private static (int Rate, string Hold) RunOnce(string bash, string prevRate, int computed, bool cut, string? hold,
        int? recoveredSecondsAgo = null) => RunWithRecovery(bash, prevRate, computed, cut, hold, recoveredSecondsAgo).Result;

    /// <summary>
    /// <see cref="RunOnce"/>, also reporting whether the run recorded a recovery.
    /// <paramref name="recoveredSecondsAgo"/> is when the rate last recovered from a cut (null: never).
    /// </summary>
    private static ((int Rate, string Hold) Result, bool Recovered) RunWithRecovery(string bash, string prevRate, int computed,
        bool cut, string? hold, int? recoveredSecondsAgo)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sqm-hyst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var holdFile = Path.Combine(dir, "hold.txt");
            var recoverFile = Path.Combine(dir, "recovered.txt");
            if (hold != null) File.WriteAllText(holdFile, hold);
            var before = recoveredSecondsAgo is { } ago ? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ago).ToString() : null;
            if (before != null) File.WriteAllText(recoverFile, before);
            var script = $@"
{ScriptGenerator.ArithmeticFunctionsText}
rate_is_valid() {{ [ -n ""$1"" ] && echo ""$1"" | grep -q '[1-9]'; }}
read_root_rate_mbps() {{ echo ""{prevRate}""; }}
HOLD_FILE='{holdFile}'
RECOVER_FILE='{recoverFile}'
HOLD_RUNS={ScriptGenerator.HoldRuns}
OSCILLATION_WINDOW={ScriptGenerator.OscillationWindowSeconds}
LATENCY_INCREASE=1.04
latency_cut={(cut ? 1 : 0)}
new_rate_int={computed}
{ScriptGenerator.HysteresisLogicText}
echo ""RATE=$new_rate_int""
";
            var psi = new ProcessStartInfo(bash) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            proc.StandardInput.Write(script.Replace("\r\n", "\n"));
            proc.StandardInput.Close();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            var rate = int.Parse(output.Split("RATE=")[1].Trim());
            var after = File.Exists(recoverFile) ? File.ReadAllText(recoverFile).Trim() : null;
            return ((rate, File.ReadAllText(holdFile).Trim()), after != null && after != before);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AOneOffCut_IsApplied_AndRecoversOnTheNextNormalRun()
    {
        if (BashPath() is not { } bash) return;

        // A speed test or a burst: nothing recovered recently, so no hold.
        RunOnce(bash, "1060", 1026, cut: true, hold: null).Should().Be((1026, "p"));
        var next = RunWithRecovery(bash, "1026", 1060, cut: false, hold: "p", recoveredSecondsAgo: null);
        next.Result.Should().Be((1060, "0"));
        next.Recovered.Should().BeTrue("the recovery is recorded, so a cut right after it reads as oscillation");
    }

    [Fact]
    public void ACutSoonAfterARecovery_IsOscillation_AndStartsTheHold()
    {
        if (BashPath() is not { } bash) return;

        RunOnce(bash, "1078", 1034, cut: true, hold: "0", recoveredSecondsAgo: 120)
            .Should().Be((1034, ScriptGenerator.HoldRuns.ToString()));
        RunOnce(bash, "1078", 1034, cut: true, hold: "0", recoveredSecondsAgo: ScriptGenerator.OscillationWindowSeconds + 60)
            .Should().Be((1034, "p"), "a cut long after the last recovery is one-off again");
    }

    [Fact]
    public void TheEndOfAClimb_IsARecovery()
    {
        if (BashPath() is not { } bash) return;

        var end = RunWithRecovery(bash, "1075", 1078, cut: false, hold: "c", recoveredSecondsAgo: null);
        end.Result.Should().Be((1078, "0"));
        end.Recovered.Should().BeTrue();
    }

    [Fact]
    public void WhileHolding_TheRateStaysAtTheCut_AndTheHoldCountsDownIntoTheClimb()
    {
        if (BashPath() is not { } bash) return;

        RunOnce(bash, "1034", 1078, cut: false, hold: "3").Should().Be((1034, "2"));
        RunOnce(bash, "1034", 1078, cut: false, hold: "1").Should().Be((1034, "c"));
    }

    [Fact]
    public void AfterTheHold_TheRateClimbsOneStep_NotToTheCeiling()
    {
        if (BashPath() is not { } bash) return;

        // 1034 x 1.04 = 1075
        RunOnce(bash, "1034", 1078, cut: false, hold: "c").Should().Be((1075, "c"));
        RunOnce(bash, "1075", 1078, cut: false, hold: "c").Should().Be((1078, "0"), "a step past the computed rate stops at it, and the climb ends");
    }

    [Fact]
    public void OutsideARecovery_ARise_IsNotLimited()
    {
        if (BashPath() is not { } bash) return;

        // UniFi rebuilt the tree at its own, much lower Smart Queues rate.
        RunOnce(bash, "300", 900, cut: false, hold: "0").Should().Be((900, "0"));
    }

    [Fact]
    public void ALowerRate_IsNeverHeldUp()
    {
        if (BashPath() is not { } bash) return;

        // The schedule moved down while holding: the hold only stops climbs.
        RunOnce(bash, "1034", 900, cut: false, hold: "2").Should().Be((900, "1"));
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("garbage", null)]
    public void NoTreeOrNoHoldFile_LeavesTheComputedRate(string prevRate, string? hold)
    {
        if (BashPath() is not { } bash) return;

        RunOnce(bash, prevRate, 1078, cut: false, hold: hold).Rate.Should().Be(1078);
    }

    [Fact]
    public void PingScript_CarriesTheTreeModelAndHoldState_AndRunsTheHysteresisBeforeApplying()
    {
        var config = new SqmConfiguration { ConnectionType = ConnectionType.Gpon, ConnectionName = "WAN", Interface = "eth4", NominalDownloadSpeed = 900, NominalUploadSpeed = 500, PingHost = "1.1.1.1" };
        config.ApplyProfileSettings(1000);
        var baseline = config.GetProfile().GetHourlyBaseline(config.CongestionSeverity);
        var boot = new ScriptGenerator(config).GenerateAllScripts(baseline).Values.Single();

        var ping = boot[boot.IndexOf("# SQM Ping Adjustment Script", StringComparison.Ordinal)..];
        ping.Should().Contain($"TREE_MODEL={ScriptGenerator.TreeModel}");
        ping.Should().Contain("HOLD_FILE=\"/data/sqm/");
        ping.Should().Contain("RECOVER_FILE=\"/data/sqm/");
        ping.Should().Contain($"OSCILLATION_WINDOW={ScriptGenerator.OscillationWindowSeconds}");
        ping.Should().Contain("latency_cut=1");
        var hysteresis = ping.IndexOf("# Hysteresis:", StringComparison.Ordinal);
        hysteresis.Should().BeGreaterThan(ping.IndexOf("read_root_rate_mbps() {", StringComparison.Ordinal), "the tc functions are defined first");
        hysteresis.Should().BeLessThan(ping.IndexOf("update_all_tc_classes $IFB_DEVICE $new_rate_int", StringComparison.Ordinal));
    }

    [Fact]
    public void FqCodelChange_LeavesUniFisPacketLimit()
    {
        var functions = ScriptGenerator.TcFunctionsText;

        functions.Should().Contain("fq_codel memory_limit $fq_mem");
        functions.Should().NotContain("calc_fq_limit");
        functions.Should().NotMatchRegex(@"fq_codel limit \$");
    }
}
