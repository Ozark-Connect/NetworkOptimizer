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

    [Fact]
    public void GenerateAllScripts_WhenLinkSpeedSet_EmbedsLinkCeilingClampInBothScripts()
    {
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth6",
            MaxDownloadSpeed = 1013,
            MinDownloadSpeed = 868,
            AbsoluteMaxDownloadSpeed = 1032,
            SafetyCapPercent = 0.95,
            PingHost = "8.8.8.8",
            WanLinkSpeedMbps = 1000
        };

        var generator = new ScriptGenerator(config);
        var baseline = new Dictionary<string, string> { ["0_12"] = "940" };
        var bootScript = generator.GenerateAllScripts(baseline).Values.First();

        var speedtest = ExtractHeredocSection(bootScript, "SPEEDTEST_EOF");
        var ping = ExtractHeredocSection(bootScript, "PING_EOF");

        Assert.Contains("WAN_LINK_SPEED_MBPS=\"1000\"", speedtest);
        Assert.Contains("WAN_LINK_SPEED_MBPS=\"1000\"", ping);
        Assert.Contains("LINK_SPEED_HEADROOM=\"0.98\"", speedtest);
        Assert.Contains("LINK_SPEED_HEADROOM=\"0.98\"", ping);
        Assert.Contains("$WAN_LINK_SPEED_MBPS * $LINK_SPEED_HEADROOM", speedtest);
        Assert.Contains("$WAN_LINK_SPEED_MBPS * $LINK_SPEED_HEADROOM", ping);
    }

    [Fact]
    public void GenerateAllScripts_SpeedtestProbeRate_IsAboveLineRate()
    {
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth6",
            MaxDownloadSpeed = 1013,
            MinDownloadSpeed = 868,
            AbsoluteMaxDownloadSpeed = 1032,
            PingHost = "8.8.8.8",
            WanLinkSpeedMbps = 1000
        };

        var generator = new ScriptGenerator(config);
        var bootScript = generator.GenerateAllScripts(new Dictionary<string, string>()).Values.First();
        var speedtest = ExtractHeredocSection(bootScript, "SPEEDTEST_EOF");

        // Probe rate = 1.05 * max(AbsoluteMax, LinkSpeed) = 1.05 * max(1032, 1000) = 1083
        Assert.Contains("SPEEDTEST_PROBE_RATE=\"1083\"", speedtest);
        // Initial TC set uses probe rate, not ABSOLUTE_MAX_DOWNLOAD_SPEED
        Assert.Contains("update_all_tc_classes $IFB_DEVICE $SPEEDTEST_PROBE_RATE", speedtest);
    }

    [Fact]
    public void GenerateAllScripts_WhenLinkSpeedUnknown_EmitsZeroAndSkipsClamp()
    {
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth6",
            MaxDownloadSpeed = 1013,
            MinDownloadSpeed = 868,
            AbsoluteMaxDownloadSpeed = 1032,
            PingHost = "8.8.8.8",
            WanLinkSpeedMbps = null
        };

        var generator = new ScriptGenerator(config);
        var bootScript = generator.GenerateAllScripts(new Dictionary<string, string>()).Values.First();

        var speedtest = ExtractHeredocSection(bootScript, "SPEEDTEST_EOF");
        var ping = ExtractHeredocSection(bootScript, "PING_EOF");

        Assert.Contains("WAN_LINK_SPEED_MBPS=\"0\"", speedtest);
        Assert.Contains("WAN_LINK_SPEED_MBPS=\"0\"", ping);
        // Guard ensures clamp only runs when link speed is known
        Assert.Contains("if [ \"$WAN_LINK_SPEED_MBPS\" -gt 0 ]", speedtest);
        Assert.Contains("if [ \"$WAN_LINK_SPEED_MBPS\" -gt 0 ]", ping);
    }

    [Fact]
    public void GenerateAllScripts_OverriddenLinkSpeed_UsesOverrideInScripts()
    {
        // Simulates a 2.5G SFP that reports as 1G - user overrides to 2500
        var config = new SqmConfiguration
        {
            ConnectionName = "Test WAN",
            Interface = "eth6",
            MaxDownloadSpeed = 1013,
            MinDownloadSpeed = 868,
            AbsoluteMaxDownloadSpeed = 1032,
            PingHost = "8.8.8.8",
            WanLinkSpeedMbps = 2500
        };

        var generator = new ScriptGenerator(config);
        var bootScript = generator.GenerateAllScripts(new Dictionary<string, string>()).Values.First();

        var speedtest = ExtractHeredocSection(bootScript, "SPEEDTEST_EOF");
        var ping = ExtractHeredocSection(bootScript, "PING_EOF");

        Assert.Contains("WAN_LINK_SPEED_MBPS=\"2500\"", speedtest);
        Assert.Contains("WAN_LINK_SPEED_MBPS=\"2500\"", ping);
        // Probe rate = 1.05 * max(1032, 2500) = 2625
        Assert.Contains("SPEEDTEST_PROBE_RATE=\"2625\"", speedtest);
    }

    [Fact]
    public void ApplyProfileSettings_WithLinkSpeedOverride_StoresOverriddenValue()
    {
        var config = new SqmConfiguration
        {
            ConnectionType = ConnectionType.Gpon,
            NominalDownloadSpeed = 965,
            NominalUploadSpeed = 50,
            Interface = "eth6"
        };

        // Apply with overridden speed (2500 instead of detected 1000)
        config.ApplyProfileSettings(wanLinkSpeedMbps: 2500);

        Assert.Equal(2500, config.WanLinkSpeedMbps);
        // Probe rate should be above 2500, not 1000
        Assert.True(config.SpeedtestProbeRateMbps > 2500);
    }

    [Fact]
    public void ApplyProfileSettings_WithNullLinkSpeed_LeavesLinkSpeedNull()
    {
        var config = new SqmConfiguration
        {
            ConnectionType = ConnectionType.Gpon,
            NominalDownloadSpeed = 965,
            NominalUploadSpeed = 50,
            Interface = "eth6"
        };

        config.ApplyProfileSettings(wanLinkSpeedMbps: null);

        Assert.Null(config.WanLinkSpeedMbps);
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
