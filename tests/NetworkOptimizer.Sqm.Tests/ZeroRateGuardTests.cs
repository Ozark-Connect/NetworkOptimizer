using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// Holds the two rules that stop a lost bc reaching tc as rate 0Mbit: no rate calculation may
/// depend on bc, and no tc write may accept a rate that is not positive.
/// Skipped silently where no bash is on the PATH.
/// </summary>
public class ZeroRateGuardTests
{
    private static string? BashPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "bash", "bash.exe" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static SqmConfiguration Config()
    {
        var config = new SqmConfiguration
        {
            ConnectionType = ConnectionType.Gpon,
            ConnectionName = "Test WAN",
            Interface = "eth4",
            NominalDownloadSpeed = 980,
            NominalUploadSpeed = 1000,
            ShapeUpload = true,
            PingHost = "1.1.1.1",
        };
        config.ApplyProfileSettings(1000);
        return config;
    }

    private static string BootScript()
    {
        var config = Config();
        return new ScriptGenerator(config)
            .GenerateAllScripts(config.GetProfile().GetHourlyBaseline(1.0))
            .Values.Single()
            .Replace("\r\n", "\n");
    }

    private static string Embedded(string boot, string marker)
    {
        var start = boot.IndexOf($"<< '{marker}'\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"the boot script embeds a {marker} heredoc");
        start = boot.IndexOf('\n', start) + 1;
        var end = boot.IndexOf($"\n{marker}\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return boot[start..end];
    }

    /// <summary>Run a script, folding stderr into stdout so failures are legible.</summary>
    private static string Run(string bash, string script)
    {
        var psi = new ProcessStartInfo(bash)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.StandardInput.NewLine = "\n";
        proc.StandardInput.Write(script.Replace("\r\n", "\n"));
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return stdout + stderr;
    }

    [Fact]
    public void GeneratedScripts_DoNotDependOnBc()
    {
        var boot = BootScript();

        boot.Should().NotContain("| bc", "no rate calculation may depend on bc");
        boot.Should().NotContain("apt-get install -y bc", "bc is no longer a dependency to install");

        foreach (var marker in new[] { "SPEEDTEST_EOF", "PING_EOF" })
        {
            var script = Embedded(boot, marker);
            script.Should().Contain("num_i()", $"{marker} defines the awk helpers it calls");
            script.Should().Contain("LC_ALL=C awk", $"{marker} pins awk's locale so a comma decimal cannot appear");
        }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("0.0", false)]
    [InlineData("00", false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("-5", false)]
    [InlineData("0.5", true)]
    [InlineData("1", true)]
    [InlineData("931", true)]
    [InlineData("931.000000", true)]
    public void UpdateAllTcClasses_WritesOnlyAPositiveRate(string rate, bool shouldWrite)
    {
        var bash = BashPath();
        if (bash == null) return;

        // tc is stubbed so the root-class write is observable without a gateway.
        var script = $@"
LOG_FILE=/dev/null
tc() {{ echo ""TC $*""; }}
{ScriptGenerator.TcFunctionsText.Replace("\r\n", "\n")}
update_all_tc_classes ifbeth4 '{rate}' 0
";
        var output = Run(bash, script);

        if (shouldWrite)
        {
            output.Should().Contain($"TC class change dev ifbeth4 parent 1: classid 1:1 htb rate {rate}Mbit",
                $"'{rate}' is a usable rate");
        }
        else
        {
            output.Should().NotContain("TC class change", $"'{rate}' must never reach tc");
        }
    }

    [Fact]
    public void UpdateAllTcClasses_LogsWhyItRefused()
    {
        var bash = BashPath();
        if (bash == null) return;

        var script = $@"
LOG_FILE=$(mktemp)
tc() {{ echo ""TC $*""; }}
{ScriptGenerator.TcFunctionsText.Replace("\r\n", "\n")}
update_all_tc_classes ifbeth4 '0' 0
cat ""$LOG_FILE""
";
        Run(bash, script).Should().Contain("refusing tc update on ifbeth4");
    }

    [Fact]
    public void UpdateAllTcClasses_SurvivesAnUnsetLogFile()
    {
        var bash = BashPath();
        if (bash == null) return;

        // The shaper-lift wrapper includes these functions and defines no LOG_FILE.
        var script = $@"
set -u
tc() {{ echo ""TC $*""; }}
{ScriptGenerator.TcFunctionsText.Replace("\r\n", "\n")}
update_all_tc_classes ifbeth4 '0' 0
echo ""SURVIVED""
";
        Run(bash, script).Should().Contain("SURVIVED");
    }

    [Fact]
    public void ClampChain_WithoutBc_StillProducesAUsableRate()
    {
        var bash = BashPath();
        if (bash == null) return;

        // The clamp chain with bc absent. This produced 0 and applied it.
        var speedtest = Embedded(BootScript(), "SPEEDTEST_EOF");
        var helpers = ScriptGenerator.ArithmeticFunctionsText.Replace("\r\n", "\n");

        var script = $@"
PATH=/usr/bin:/bin
bc() {{ echo ""bc must not be called"" >&2; return 127; }}
export -f bc
{helpers}
MAX_DOWNLOAD_SPEED=980
MIN_DOWNLOAD_SPEED=50
SAFETY_CAP=0.95
WAN_LINK_SPEED_MBPS=1000
LINK_SPEED_HEADROOM=0.98
download_speed_bytes=118000000

download_speed_mbps=$(num_i ""$download_speed_bytes * 8 / 1000000"")
download_speed_mbps=$((download_speed_mbps < MIN_DOWNLOAD_SPEED ? MIN_DOWNLOAD_SPEED : download_speed_mbps))
download_speed_mbps=$((download_speed_mbps > MAX_DOWNLOAD_SPEED ? MAX_DOWNLOAD_SPEED : download_speed_mbps))
max_adjusted_rate=$(num_i ""$MAX_DOWNLOAD_SPEED * $SAFETY_CAP"")
download_speed_mbps=$((download_speed_mbps > max_adjusted_rate ? max_adjusted_rate : download_speed_mbps))
link_ceiling=$(num_i ""$WAN_LINK_SPEED_MBPS * $LINK_SPEED_HEADROOM"")
if [ ""$download_speed_mbps"" -gt ""$link_ceiling"" ]; then download_speed_mbps=$link_ceiling; fi
echo ""RATE=$download_speed_mbps""
";
        var output = Run(bash, script);

        output.Should().Contain("RATE=931", "944 Mbps measured, clamped by the 95% safety cap on 980");
        output.Should().NotContain("RATE=0");
        output.Should().NotContain("bc must not be called");

        // And the shipped script keeps the guard that would catch it even if the maths regressed.
        speedtest.Should().Contain("rate_is_valid \"$download_speed_mbps\"");
    }

    [Fact]
    public void CalibrationScript_ValidatesBeforeItWritesTheResultFile()
    {
        var speedtest = Embedded(BootScript(), "SPEEDTEST_EOF");

        // A 0 in the result file reads back as the last good measurement.
        var guard = speedtest.IndexOf("rate_is_valid \"$download_speed_mbps\"", StringComparison.Ordinal);
        var write = speedtest.IndexOf("echo \"Measured download speed:", StringComparison.Ordinal);
        var apply = speedtest.IndexOf("update_all_tc_classes $IFB_DEVICE $download_speed_mbps", StringComparison.Ordinal);

        guard.Should().BeGreaterThan(0);
        guard.Should().BeLessThan(write, "the result file is written only once the rate is known good");
        guard.Should().BeLessThan(apply, "tc is written only once the rate is known good");
    }

    [Fact]
    public void CalibrationScript_RestoresRatherThanLeavingTheProbeRate()
    {
        var speedtest = Embedded(BootScript(), "SPEEDTEST_EOF");

        // Bailing after the probe-rate lift without restoring leaves download unshaped.
        speedtest.Should().Contain("Restoring last good rate");
        speedtest.Should().Contain("update_all_tc_classes $IFB_DEVICE $previous_rate $DOWNLOAD_BURST_MODE");

        var lift = speedtest.IndexOf("update_all_tc_classes $IFB_DEVICE $SPEEDTEST_PROBE_RATE", StringComparison.Ordinal);
        var deps = speedtest.IndexOf("for dep in awk jq; do", StringComparison.Ordinal);
        deps.Should().BeGreaterThan(0);
        deps.Should().BeLessThan(lift, "a missing dependency must bail before tc is touched at all");
    }

    [Fact]
    public void PingScript_ChecksAwkButNeverInstalls()
    {
        var ping = Embedded(BootScript(), "PING_EOF");

        ping.Should().Contain("if ! which awk > /dev/null 2>&1; then");
        // An apt-get every minute is its own outage.
        ping.Should().NotContain("apt-get");
    }
}
