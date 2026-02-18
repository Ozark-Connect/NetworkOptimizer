using System.Globalization;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class ScriptGeneratorTests
{
    [Fact]
    public void GenerateAllScripts_WithGermanLocale_UsesDecimalPointNotComma()
    {
        // Arrange - save current culture and set to German (uses comma as decimal separator)
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var config = new SqmConfiguration
            {
                ConnectionName = "Test WAN",
                Interface = "eth0",
                BaselineLatency = 17.9,
                LatencyThreshold = 2.5,
                LatencyDecrease = 0.97,
                LatencyIncrease = 1.04,
                OverheadMultiplier = 1.05,
                BlendingWeightWithin = 0.6,
                BlendingWeightBelow = 0.8,
                MaxDownloadSpeed = 100,
                MinDownloadSpeed = 50,
                AbsoluteMaxDownloadSpeed = 110,
                PingHost = "8.8.8.8"
            };

            var generator = new ScriptGenerator(config);
            var baseline = new Dictionary<string, string> { ["0_12"] = "95" };

            // Act
            var scripts = generator.GenerateAllScripts(baseline);

            // Assert - verify scripts use decimal points, not commas
            var bootScript = scripts.Values.First();

            // Check all double values use decimal point
            Assert.Contains("BASELINE_LATENCY=17.9", bootScript);
            Assert.Contains("LATENCY_THRESHOLD=2.5", bootScript);
            Assert.Contains("LATENCY_DECREASE=0.97", bootScript);
            Assert.Contains("LATENCY_INCREASE=1.04", bootScript);
            Assert.Contains("DOWNLOAD_SPEED_MULTIPLIER=\"1.05\"", bootScript);

            // Verify no commas in numeric assignments (would break bc)
            Assert.DoesNotContain("BASELINE_LATENCY=17,9", bootScript);
            Assert.DoesNotContain("LATENCY_THRESHOLD=2,5", bootScript);
            Assert.DoesNotContain("LATENCY_DECREASE=0,97", bootScript);
            Assert.DoesNotContain("* 1,05", bootScript);
            Assert.DoesNotContain("* 0,6", bootScript);
        }
        finally
        {
            // Restore original culture
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void GenerateAllScripts_BlendingWeights_NoFloatingPointArtifacts()
    {
        // Arrange
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth0",
            BlendingWeightWithin = 0.7,  // 1.0 - 0.7 can produce 0.30000000000000004
            BlendingWeightBelow = 0.8,
            MaxDownloadSpeed = 100,
            MinDownloadSpeed = 50,
            AbsoluteMaxDownloadSpeed = 110,
            PingHost = "8.8.8.8"
        };

        var generator = new ScriptGenerator(config);
        var baseline = new Dictionary<string, string> { ["0_12"] = "95" };

        // Act
        var scripts = generator.GenerateAllScripts(baseline);
        var bootScript = scripts.Values.First();

        // Assert - should have clean 0.3, not 0.30000000000000004
        Assert.Contains("* 0.3)", bootScript);
        Assert.DoesNotContain("0.30000000000000004", bootScript);
    }

    [Fact]
    public void GenerateAllScripts_ContainsIfbDeviceCheck_InBothScripts()
    {
        // Arrange
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth3",
            MaxDownloadSpeed = 100,
            MinDownloadSpeed = 50,
            AbsoluteMaxDownloadSpeed = 110,
            PingHost = "8.8.8.8"
        };

        var generator = new ScriptGenerator(config);
        var baseline = new Dictionary<string, string> { ["0_12"] = "95" };

        // Act
        var scripts = generator.GenerateAllScripts(baseline);
        var bootScript = scripts.Values.First();

        // Extract the speedtest and ping script sections from the heredocs
        var speedtestSection = ExtractHeredocSection(bootScript, "SPEEDTEST_EOF");
        var pingSection = ExtractHeredocSection(bootScript, "PING_EOF");

        // Assert - IFB check appears in BOTH embedded scripts independently
        Assert.Contains("ip link show \"$IFB_DEVICE\"", speedtestSection);
        Assert.Contains("ip link show \"$IFB_DEVICE\"", pingSection);

        // Both scripts define the correct IFB device name
        Assert.Contains("IFB_DEVICE=\"ifbeth3\"", speedtestSection);
        Assert.Contains("IFB_DEVICE=\"ifbeth3\"", pingSection);

        // Both scripts exit with error code 1 on missing IFB
        Assert.Contains("exit 1", speedtestSection);
        Assert.Contains("exit 1", pingSection);
    }

    [Fact]
    public void GenerateAllScripts_IfbCheck_AppearsBeforeTcCommands()
    {
        // Arrange
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth2",
            MaxDownloadSpeed = 100,
            MinDownloadSpeed = 50,
            AbsoluteMaxDownloadSpeed = 110,
            PingHost = "8.8.8.8"
        };

        var generator = new ScriptGenerator(config);
        var baseline = new Dictionary<string, string> { ["0_12"] = "95" };

        // Act
        var scripts = generator.GenerateAllScripts(baseline);
        var bootScript = scripts.Values.First();

        var speedtestSection = ExtractHeredocSection(bootScript, "SPEEDTEST_EOF");
        var pingSection = ExtractHeredocSection(bootScript, "PING_EOF");

        // Assert - IFB check must come BEFORE any tc usage in both scripts
        var ifbCheckInSpeedtest = speedtestSection.IndexOf("ip link show \"$IFB_DEVICE\"");
        var firstTcInSpeedtest = speedtestSection.IndexOf("update_all_tc_classes");
        Assert.True(ifbCheckInSpeedtest >= 0, "IFB check missing from speedtest script");
        Assert.True(firstTcInSpeedtest >= 0, "tc command missing from speedtest script");
        Assert.True(ifbCheckInSpeedtest < firstTcInSpeedtest,
            "IFB check must appear before the first tc command in speedtest script");

        var ifbCheckInPing = pingSection.IndexOf("ip link show \"$IFB_DEVICE\"");
        var firstTcInPing = pingSection.IndexOf("update_all_tc_classes");
        Assert.True(ifbCheckInPing >= 0, "IFB check missing from ping script");
        Assert.True(firstTcInPing >= 0, "tc command missing from ping script");
        Assert.True(ifbCheckInPing < firstTcInPing,
            "IFB check must appear before the first tc command in ping script");
    }

    [Theory]
    [InlineData("eth0", "ifbeth0")]
    [InlineData("eth2", "ifbeth2")]
    [InlineData("eth3", "ifbeth3")]
    [InlineData("eth4", "ifbeth4")]
    public void GenerateAllScripts_IfbDeviceName_DerivedFromInterface(string iface, string expectedIfb)
    {
        // Arrange
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = iface,
            MaxDownloadSpeed = 100,
            MinDownloadSpeed = 50,
            AbsoluteMaxDownloadSpeed = 110,
            PingHost = "8.8.8.8"
        };

        var generator = new ScriptGenerator(config);
        var baseline = new Dictionary<string, string> { ["0_12"] = "95" };

        // Act
        var scripts = generator.GenerateAllScripts(baseline);
        var bootScript = scripts.Values.First();

        // Assert - IFB device name is correctly derived for each interface
        Assert.Contains($"IFB_DEVICE=\"{expectedIfb}\"", bootScript);
    }

    /// <summary>
    /// Extracts the content between heredoc delimiters (e.g., between 'SPEEDTEST_EOF' markers).
    /// </summary>
    private static string ExtractHeredocSection(string script, string delimiter)
    {
        var startMarker = $"<< '{delimiter}'";
        var startIdx = script.IndexOf(startMarker);
        Assert.True(startIdx >= 0, $"Heredoc start marker '{startMarker}' not found");

        var contentStart = script.IndexOf('\n', startIdx) + 1;
        var endIdx = script.IndexOf($"\n{delimiter}\n", contentStart);
        if (endIdx < 0)
            endIdx = script.IndexOf($"\n{delimiter}", contentStart);

        Assert.True(endIdx >= 0, $"Heredoc end marker '{delimiter}' not found");

        return script[contentStart..endIdx];
    }
}
