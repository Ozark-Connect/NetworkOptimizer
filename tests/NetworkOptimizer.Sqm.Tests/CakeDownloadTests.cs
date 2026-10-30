using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// The opt-in CAKE download shaper: what the generator emits, and how the tc functions behave against
/// a stubbed tc that reports either UniFi's HTB tree or Adaptive SQM's CAKE root on the IFB.
/// </summary>
public class CakeDownloadTests
{
    private const string HtbIfb = "qdisc htb 1: root refcnt 2 r2q 10 default 0x10 direct_packets_stat 0";
    private const string CakeIfb = "qdisc cake ca4e: root refcnt 2 bandwidth 700Mbit besteffort dual-dsthost nat nowash ingress no-ack-filter no-split-gso rtt 100ms noatm overhead 38 mpu 84";
    private const string HtbRootClass = "class htb 1:1 root rate 800Mbit ceil 800Mbit burst 4000b cburst 4000b";

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

    /// <summary>
    /// A tc that answers "qdisc show" and "class show" for the IFB from canned output and echoes every
    /// write, so the functions can run without a gateway.
    /// </summary>
    private static string TcStub(string ifbQdisc, string ifbClasses) => $@"
tc() {{
    case ""$1 $2"" in
        ""qdisc show"") [ ""$4"" = ""ifbeth4"" ] && printf '%s\n' ""{ifbQdisc}"" ;;
        ""class show"") [ ""$4"" = ""ifbeth4"" ] && printf '%b\n' ""{ifbClasses}"" ;;
        *) echo ""TC $*"" ;;
    esac
}}
modprobe() {{ :; }}
";

    private static string Functions(string qdisc, string opts = "besteffort ingress dual-dsthost nat no-split-gso overhead 38 mpu 84") => $@"
LOG_FILE=/dev/stdout
IFB_DEVICE=ifbeth4
DOWNLOAD_QDISC={qdisc}
CAKE_DOWNLOAD_OPTS=""{opts}""
{ScriptGenerator.TcFunctionsText.Replace("\r\n", "\n")}
";

    private static SqmConfiguration Config(bool cakeDownload, string iface = "eth4")
    {
        var config = new SqmConfiguration
        {
            ConnectionType = ConnectionType.Gpon,
            ConnectionName = "Test WAN",
            Interface = iface,
            NominalDownloadSpeed = 900,
            NominalUploadSpeed = 500,
            ShapeUpload = true,
            PingHost = "1.1.1.1",
            CakeDownload = cakeDownload,
        };
        config.ApplyProfileSettings(1000);
        return config;
    }

    private static string BootScript(SqmConfiguration config)
    {
        var baseline = config.GetProfile().GetHourlyBaseline(config.CongestionSeverity);
        return new ScriptGenerator(config).GenerateAllScripts(baseline).Values.Single().Replace("\r\n", "\n");
    }

    private static string Embedded(string boot, string marker)
    {
        var start = boot.IndexOf($"<< '{marker}'\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"the boot script embeds a {marker} heredoc");
        start = boot.IndexOf('\n', start) + 1;
        var end = boot.IndexOf($"\n{marker}\n", start, StringComparison.Ordinal);
        return boot[start..end];
    }

    [Fact]
    public void CakeDownload_IsOffByDefault()
    {
        new SqmConfiguration().CakeDownload.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "htb")]
    [InlineData(true, "cake")]
    public void EveryHeredoc_DefinesTheDownloadQdiscAndCakeOptions(bool cakeDownload, string expected)
    {
        var boot = BootScript(Config(cakeDownload));

        // Each heredoc is its own script on the gateway; one must not lean on the other's definition.
        foreach (var marker in new[] { "SPEEDTEST_EOF", "PING_EOF" })
        {
            var script = Embedded(boot, marker);
            script.Should().Contain($"DOWNLOAD_QDISC=\"{expected}\"\n", $"{marker} selects the download qdisc");
            script.Should().Contain("CAKE_DOWNLOAD_OPTS=\"besteffort ingress dual-dsthost nat no-split-gso overhead 38 mpu 84\"\n",
                $"{marker} carries the CAKE options");
        }
    }

    [Theory]
    [InlineData("ppp0", 50)]
    [InlineData("eth4.7", 42)]
    [InlineData("eth4", 38)]
    [InlineData("sfp0", 38)]
    public void CakeOverhead_FollowsTheWanEncapsulation(string iface, int overhead)
    {
        ScriptGenerator.CakeOverheadBytes(iface).Should().Be(overhead);
        ScriptGenerator.CakeDownloadOptions(iface).Should()
            .Be($"besteffort ingress dual-dsthost nat no-split-gso overhead {overhead} mpu 84");
    }

    [Fact]
    public void TcFunctions_CarryTheHandleTheRemovalLooksFor()
    {
        ScriptGenerator.TcFunctionsText.Should().Contain($"CAKE_HANDLE=\"{ScriptGenerator.CakeHandle}\"");
    }

    [Fact]
    public void CakeOptIn_OverUniFisHtbTree_ReplacesTheRoot()
    {
        var bash = BashPath();
        if (bash == null) return;

        var output = Run(bash, TcStub(HtbIfb, HtbRootClass) + Functions("cake") +
            "update_all_tc_classes ifbeth4 900 1\n");

        output.Should().Contain("TC qdisc replace dev ifbeth4 root handle ca4e: cake bandwidth 900Mbit " +
            "besteffort ingress dual-dsthost nat no-split-gso overhead 38 mpu 84 memlimit 8388608");
        output.Should().Contain("CAKE installed on ifbeth4 at 900 Mbps");
        output.Should().NotContain("TC class change", "CAKE replaces the HTB tree instead of retuning it");
    }

    [Fact]
    public void CakeOptIn_OnItsOwnRoot_ChangesTheBandwidthAndRestatesEveryOption()
    {
        var bash = BashPath();
        if (bash == null) return;

        var output = Run(bash, TcStub(CakeIfb, "") + Functions("cake") +
            "update_all_tc_classes ifbeth4 400 1\n");

        // Restating no-split-gso on every change keeps the kernel from re-enabling GSO splitting.
        output.Should().Contain("TC qdisc change dev ifbeth4 root cake bandwidth 400Mbit " +
            "besteffort ingress dual-dsthost nat no-split-gso overhead 38 mpu 84 memlimit 5126304");
        output.Should().NotContain("replace");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("abc")]
    public void CakeOptIn_RefusesAnUnusableRate(string rate)
    {
        var bash = BashPath();
        if (bash == null) return;

        var output = Run(bash, TcStub(CakeIfb, "") + Functions("cake") +
            $"update_all_tc_classes ifbeth4 '{rate}' 1\n");

        output.Should().NotContain("TC qdisc");
        output.Should().Contain("refusing tc update on ifbeth4");
    }

    [Fact]
    public void CakeOptIn_NeverTouchesTheWanDevice()
    {
        var bash = BashPath();
        if (bash == null) return;

        var output = Run(bash, TcStub(HtbIfb, HtbRootClass) + Functions("cake") +
            "update_all_tc_classes eth4 500\n");

        output.Should().Contain("TC class change dev eth4 parent 1: classid 1:1 htb rate 500Mbit");
        output.Should().NotContain("cake");
    }

    [Fact]
    public void CakeOptIn_WarnsWhenUniFiQosClassesAreReplaced()
    {
        var bash = BashPath();
        if (bash == null) return;

        var classes = HtbRootClass + "\\nclass htb 1:10 parent 1:1 leaf 8001: prio 0 rate 50Mbit ceil 800Mbit" +
            "\\nclass htb 1:11 parent 1:1 leaf 8002: prio 3 rate 64bit ceil 800Mbit";
        var output = Run(bash, TcStub(HtbIfb, classes) + Functions("cake") +
            "update_all_tc_classes ifbeth4 900\n");

        output.Should().Contain("WARNING: 1 UniFi QoS class(es) on ifbeth4 do not apply under CAKE");
    }

    [Theory]
    [InlineData("htb")]
    [InlineData("")]
    public void WithoutTheOptIn_TheIfbStaysHtb(string qdisc)
    {
        var bash = BashPath();
        if (bash == null) return;

        // An empty value stands for a script deployed before the option existed.
        var output = Run(bash, TcStub(HtbIfb, HtbRootClass) + Functions(qdisc) +
            "update_all_tc_classes ifbeth4 900\n");

        output.Should().Contain("TC class change dev ifbeth4 parent 1: classid 1:1 htb rate 900Mbit");
        output.Should().NotContain("cake");
    }

    [Fact]
    public void ExistingMode_RetunesACakeRoot_AndNeverInstallsOne()
    {
        var bash = BashPath();
        if (bash == null) return;

        var onCake = Run(bash, TcStub(CakeIfb, "") + Functions("existing") +
            "update_all_tc_classes ifbeth4 1030 1\n");
        onCake.Should().Contain("TC qdisc change dev ifbeth4 root cake bandwidth 1030Mbit");

        var onHtb = Run(bash, TcStub(HtbIfb, HtbRootClass) + Functions("existing") +
            "update_all_tc_classes ifbeth4 1030 1\n");
        onHtb.Should().Contain("TC class change dev ifbeth4 parent 1: classid 1:1 htb rate 1030Mbit");
        onHtb.Should().NotContain("cake");
    }

    [Theory]
    [InlineData(HtbIfb, HtbRootClass, "800")]
    [InlineData(CakeIfb, "", "700")]
    [InlineData("qdisc cake ca4e: root refcnt 2 bandwidth 1Gbit besteffort", "", "1000")]
    [InlineData("qdisc cake ca4e: root refcnt 2 bandwidth unlimited besteffort", "", "")]
    [InlineData("qdisc noqueue 0: root refcnt 2", "", "")]
    public void ReadRootRate_ReadsHtbClassOrCakeBandwidth(string qdisc, string classes, string expected)
    {
        var bash = BashPath();
        if (bash == null) return;

        var output = Run(bash, TcStub(qdisc, classes) + Functions("htb") +
            "echo \"RATE=[$(read_root_rate_mbps ifbeth4)]\"\n");

        output.Should().Contain($"RATE=[{expected}]");
    }

    [Fact]
    public void PingScript_ReinstallsCake_WhenUniFiRebuiltHtbAtTheSameRate()
    {
        var bash = BashPath();
        if (bash == null) return;

        var dir = Path.Combine(Path.GetTempPath(), $"sqm-cake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = Config(cakeDownload: true);
            var ping = Embedded(BootScript(config), "PING_EOF")
                .Replace("/data/sqm/", dir + "/")
                .Replace("/var/log/", dir + "/");
            File.WriteAllText(Path.Combine(dir, "test-wan-result.txt"), "Measured download speed: 800 Mbps\n");

            // UniFi has just rebuilt its HTB tree at exactly the rate the loop would ask for. The
            // latency sits in the hold band, so the computed rate is whatever the start rate is;
            // the root class reports that same rate back.
            var script = $@"
ping() {{ echo 'rtt min/avg/max/mdev = 1/{config.BaselineLatency + 0.1}/6/1 ms'; }}
ip() {{ return 0; }}
pgrep() {{ return 1; }}
RATE_FILE={dir}/rate
tc() {{
    case ""$1 $2"" in
        ""qdisc show"") [ ""$4"" = ""ifbeth4"" ] && echo ""{HtbIfb}"" ;;
        ""class show"") [ ""$4"" = ""ifbeth4"" ] && echo ""class htb 1:1 root rate $(cat $RATE_FILE 2>/dev/null)Mbit ceil 1Mbit"" ;;
        *) echo ""TC $*"" ;;
    esac
}}
modprobe() {{ :; }}
export -f ping ip pgrep tc modprobe
";
            // First run: learn the rate the loop settles on with HTB answering at a different rate.
            File.WriteAllText(Path.Combine(dir, "rate"), "1");
            var first = Run(bash, script + ping);
            var installed = System.Text.RegularExpressions.Regex.Match(first, @"replace dev ifbeth4 root handle ca4e: cake bandwidth (\d+)Mbit");
            installed.Success.Should().BeTrue($"the first run installs CAKE: {first}");

            // Second run: HTB now reports exactly that rate. Same rate must not read as unchanged.
            File.WriteAllText(Path.Combine(dir, "rate"), installed.Groups[1].Value);
            var second = Run(bash, script + ping);
            second.Should().Contain($"replace dev ifbeth4 root handle ca4e: cake bandwidth {installed.Groups[1].Value}Mbit");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
