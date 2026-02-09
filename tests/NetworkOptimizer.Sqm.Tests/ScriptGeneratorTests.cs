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
}
