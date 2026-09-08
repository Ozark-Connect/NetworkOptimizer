using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// Dynamic upload shaping and learned profiles must be strictly additive: a configuration that does
/// not opt in produces the scripts it always did, byte for byte.
/// </summary>
public class DynamicUploadTests
{
    private static SqmConfiguration Config(ConnectionType type = ConnectionType.CellularHome, double uploadSeverity = 0.0)
    {
        var config = new SqmConfiguration
        {
            ConnectionType = type,
            ConnectionName = "Test WAN",
            Interface = "eth4",
            NominalDownloadSpeed = 200,
            NominalUploadSpeed = 30,
            ShapeUpload = true,
            PingHost = "1.1.1.1",
            UploadCongestionSeverity = uploadSeverity,
        };
        config.ApplyProfileSettings(1000);
        return config;
    }

    private static Dictionary<string, string> Baseline(SqmConfiguration config) =>
        config.GetProfile().GetHourlyBaseline(config.CongestionSeverity);

    [Fact]
    public void StrengthZero_LeavesTheScriptsUnchanged()
    {
        var config = Config();
        var baseline = Baseline(config);
        var uploadBaseline = config.GetProfile().GetHourlyUploadBaseline(0.5);

        var before = new ScriptGenerator(config).GenerateAllScripts(baseline).Values.Single();
        var withIgnoredUpload = new ScriptGenerator(config).GenerateAllScripts(baseline, uploadBaseline).Values.Single();

        withIgnoredUpload.Should().Be(before);
        before.Should().NotContain("UPLOAD_BASELINE");
        before.Should().NotContain("MIN_UPLOAD_SPEED");
        before.Should().Contain("update_all_tc_classes $INTERFACE $UPLOAD_SPEED");
    }

    [Fact]
    public void DefaultConfiguration_HasStaticUpload()
    {
        new SqmConfiguration().UploadCongestionSeverity.Should().Be(0.0);
        new SqmConfiguration { ShapeUpload = true }.DynamicUpload.Should().BeFalse();
    }

    [Fact]
    public void WithStrength_ScriptsCarryTheUploadScheduleAndFloor()
    {
        var config = Config(uploadSeverity: 0.5);
        var baseline = Baseline(config);
        var uploadBaseline = config.GetProfile().GetHourlyUploadBaseline(config.UploadCongestionSeverity);

        var script = new ScriptGenerator(config).GenerateAllScripts(baseline, uploadBaseline).Values.Single();

        script.Should().Contain("declare -A UPLOAD_BASELINE");
        script.Should().Contain("UPLOAD_BASELINE[0_20]=");
        script.Should().Contain($"MIN_UPLOAD_SPEED=\"{config.MinUploadSpeed}\"");
        // Both apply phases (speedtest result, ping adjustment) shape upload from the schedule; the
        // speedtest's probe phase still opens upload to nominal so the measurement runs unshaped.
        CountOf(script, "update_all_tc_classes $INTERFACE $upload_rate").Should().Be(2);
        CountOf(script, "update_all_tc_classes $INTERFACE $UPLOAD_SPEED").Should().Be(1);
        script.IndexOf("update_all_tc_classes $INTERFACE $UPLOAD_SPEED", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("speedtest_output=$(speedtest", StringComparison.Ordinal));
        // The ping script also follows a latency cut and only skips when both directions are unchanged.
        script.Should().Contain("upload_rate=$(echo \"scale=0; $upload_rate * $new_rate / $MAX_DOWNLOAD_SPEED\" | bc)");
        script.Should().Contain("[ \"$upload_rate\" = \"$current_up_rate\" ]");
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public void UploadScheduleNeverDropsBelowHalfNominal_OrAboveNominal()
    {
        var profile = new ConnectionProfile { Type = ConnectionType.Starlink, NominalDownloadMbps = 400, NominalUploadMbps = 40 };

        var schedule = profile.GetHourlyUploadBaseline(1.0).Values.Select(int.Parse).ToList();

        schedule.Min().Should().BeGreaterThanOrEqualTo(20);
        schedule.Max().Should().BeLessThanOrEqualTo(40);
        schedule.Should().HaveCount(168);
    }

    [Fact]
    public void StrengthZero_UploadScheduleIsNominalEverywhere()
    {
        var profile = new ConnectionProfile { Type = ConnectionType.CellularHome, NominalDownloadMbps = 200, NominalUploadMbps = 30 };

        profile.GetHourlyUploadBaseline(0.0).Values.Should().AllBe("30");
    }

    [Theory]
    [InlineData(ConnectionType.CellularHome, true, 0.5)]
    [InlineData(ConnectionType.Starlink, true, 0.5)]
    [InlineData(ConnectionType.FixedWireless, true, 0.5)]
    [InlineData(ConnectionType.Gpon, false, 0.0)]
    [InlineData(ConnectionType.XgsPon, false, 0.0)]
    [InlineData(ConnectionType.DocsisCable, false, 0.0)]
    [InlineData(ConnectionType.Dsl, false, 0.0)]
    public void MediumDefaults(ConnectionType type, bool supports, double defaultStrength)
    {
        ConnectionProfile.SupportsDynamicUpload(type).Should().Be(supports);
        ConnectionProfile.DefaultUploadCongestionSeverity(type).Should().Be(defaultStrength);
    }

    [Fact]
    public void EffectiveUploadMultiplier_ScalesTheDipAndFloorsAtHalf()
    {
        ConnectionProfile.EffectiveUploadMultiplier(0.5, 0.0).Should().Be(1.0);
        ConnectionProfile.EffectiveUploadMultiplier(0.5, 0.5).Should().Be(0.75);
        ConnectionProfile.EffectiveUploadMultiplier(0.5, 1.0).Should().Be(0.5);
        ConnectionProfile.EffectiveUploadMultiplier(0.2, 1.0).Should().Be(ConnectionProfile.MinUploadFraction);
    }

    [Fact]
    public void LearnedPatterns_ReplaceTheBuiltInCurve()
    {
        var config = Config(ConnectionType.Gpon);
        var learned = new double[7, 24];
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++)
                learned[d, h] = h == 20 ? 0.5 : 1.0;
        config.LearnedDownloadPattern = learned;

        var baseline = Baseline(config);

        baseline["0_20"].Should().Be("100");
        baseline["0_3"].Should().Be("200");
        // Severity still tunes a learned curve.
        config.GetProfile().GetHourlyBaseline(0.5)["0_20"].Should().Be("150");
        // Without a learned upload curve, upload follows the learned download curve.
        config.GetProfile().GetHourlyUploadBaseline(1.0)["0_20"].Should().Be("15");
    }

    [Fact]
    public void ParameterSummary_NamesUploadModeAndProfileSource()
    {
        Config().GetParameterSummary().Should().Contain("Upload: static").And.Contain("Profile: connection type default");
        Config(uploadSeverity: 0.5).GetParameterSummary().Should().Contain("Upload: dynamic, strength 0.50, floor 15 Mbps");
    }
}
