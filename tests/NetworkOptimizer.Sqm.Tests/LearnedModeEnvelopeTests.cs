using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// A deployed learned curve derives its envelope from the measured peaks: the type's shaper factor
/// turns payload into a tc rate, and nominal, the overhead buffer, and the absolute max stop applying.
/// </summary>
public class LearnedModeEnvelopeTests
{
    private static SqmConfiguration Config(ConnectionType type, int nominalDown, int nominalUp, double uploadSeverity = 0.0)
    {
        var config = new SqmConfiguration
        {
            ConnectionType = type,
            ConnectionName = "Test WAN",
            Interface = "eth4",
            NominalDownloadSpeed = nominalDown,
            NominalUploadSpeed = nominalUp,
            ShapeUpload = true,
            PingHost = "1.1.1.1",
            UploadCongestionSeverity = uploadSeverity,
            CongestionSeverity = 0.62,
        };
        config.ApplyProfileSettings(null);
        return config;
    }

    private static LearnedCongestionProfile Learned(double peakDown, double peakUp, double eveningDip = 0.8)
    {
        var down = new double[LearnedCongestionProfile.Slots];
        var up = new double[LearnedCongestionProfile.Slots];
        for (var slot = 0; slot < LearnedCongestionProfile.Slots; slot++)
        {
            var hour = slot % 24;
            down[slot] = hour == 20 ? eveningDip : 1.0;
            up[slot] = hour == 20 ? 0.9 : 1.0;
        }
        return new LearnedCongestionProfile
        {
            DownloadMultipliers = down,
            UploadMultipliers = up,
            PeakDownloadMbps = peakDown,
            PeakUploadMbps = peakUp,
        };
    }

    [Theory]
    [InlineData(ConnectionType.Gpon, 1.03)]
    [InlineData(ConnectionType.XgsPon, 1.03)]
    [InlineData(ConnectionType.DocsisCable, 1.01)]
    [InlineData(ConnectionType.Dsl, 1.01)]
    [InlineData(ConnectionType.CellularHome, 1.00)]
    [InlineData(ConnectionType.Starlink, 1.00)]
    [InlineData(ConnectionType.FixedWireless, 1.00)]
    public void ShaperFactor_IsPerAccessType(ConnectionType type, double expected)
    {
        LearnedCongestionProfile.ShaperFactor(type).Should().Be(expected);
    }

    [Fact]
    public void ShapingCeiling_IsTheMeasuredPeakAsAShaperRate()
    {
        // The cable line that measured 258 through a lifted shaper shapes at 260, where its log sat.
        LearnedCongestionProfile.ShapingCeiling(258.5, ConnectionType.DocsisCable).Should().Be(261);
        LearnedCongestionProfile.ShapingCeiling(258, ConnectionType.DocsisCable).Should().Be(261);
        LearnedCongestionProfile.ShapingCeiling(1010, ConnectionType.Gpon).Should().Be(1040);
        LearnedCongestionProfile.ShapingCeiling(200, ConnectionType.CellularHome).Should().Be(200);
    }

    [Fact]
    public void ApplyLearnedProfile_DerivesTheEnvelopeFromTheMeasuredPeak_NotNominal()
    {
        // A plan quoted at 280 on a line that delivers 258.
        var config = Config(ConnectionType.DocsisCable, 280, 28);
        config.ApplyLearnedProfile(Learned(258, 28));

        config.LearnedMode.Should().BeTrue();
        config.NominalDownloadSpeed.Should().Be(261);
        config.MaxDownloadSpeed.Should().Be(261);
        config.AbsoluteMaxDownloadSpeed.Should().Be(261);
        config.OverheadMultiplier.Should().Be(1.0);
        config.SafetyCapPercent.Should().Be(1.0);
        config.MeasuredToShapedFactor.Should().Be(1.01);
        // The floor follows the type's ratio of the new ceiling (DOCSIS 65%).
        config.MinDownloadSpeed.Should().Be((int)(261 * 0.65));
        // Severity is the user's knob and keeps scaling the learned dips.
        config.CongestionSeverity.Should().Be(0.62);
    }

    [Fact]
    public void ApplyLearnedProfile_EveryHourIsTheCeilingTimesItsMultiplier()
    {
        var config = Config(ConnectionType.Gpon, 1043, 990);
        config.CongestionSeverity = 1.0;
        config.ApplyLearnedProfile(Learned(1010, 950));

        var baseline = config.GetProfile().GetHourlyBaseline(config.CongestionSeverity);
        baseline["0_3"].Should().Be("1040");
        baseline["0_20"].Should().Be(((int)(0.8 * 1040)).ToString());
    }

    [Fact]
    public void ApplyLearnedProfile_UploadCeilingOnlyWhenUploadIsDynamic()
    {
        var off = Config(ConnectionType.CellularHome, 200, 30);
        off.ApplyLearnedProfile(Learned(180, 25));
        off.NominalUploadSpeed.Should().Be(30, "static upload stays at the user's nominal");

        var on = Config(ConnectionType.CellularHome, 200, 30, uploadSeverity: 0.5);
        on.ApplyLearnedProfile(Learned(180, 25));
        on.NominalUploadSpeed.Should().Be(25);
        on.UploadCongestionSeverity.Should().Be(0.5, "strength stays the user's knob");
        on.MinUploadSpeed.Should().Be(12);
    }

    [Fact]
    public void SpeedtestScript_ConvertsThePayloadFigure_OnlyInLearnedMode()
    {
        var learned = Config(ConnectionType.DocsisCable, 280, 28);
        learned.ApplyLearnedProfile(Learned(258, 28));
        var scripts = new ScriptGenerator(learned).GenerateAllScripts(learned.GetProfile().GetHourlyBaseline(learned.CongestionSeverity));
        var boot = scripts.Values.Single();

        boot.Should().Contain("MEASURED_TO_SHAPED=\"1.01\"");
        boot.Should().Contain("download_speed_mbps * $MEASURED_TO_SHAPED");
        // The conversion happens before the floor, so the blend compares like with like.
        boot.IndexOf("$MEASURED_TO_SHAPED /").Should().BeLessThan(boot.IndexOf("# Apply minimum floor"));

        var plain = Config(ConnectionType.DocsisCable, 280, 28);
        var plainBoot = new ScriptGenerator(plain).GenerateAllScripts(plain.GetProfile().GetHourlyBaseline(plain.CongestionSeverity)).Values.Single();
        plainBoot.Should().NotContain("MEASURED_TO_SHAPED");
    }

    [Fact]
    public void SharedMedia_ShapeAtTheMeasuredFigure()
    {
        var config = Config(ConnectionType.Starlink, 200, 20);
        config.ApplyLearnedProfile(Learned(150, 12));

        config.MaxDownloadSpeed.Should().Be(150);
        config.MeasuredToShapedFactor.Should().Be(1.0);
        var boot = new ScriptGenerator(config).GenerateAllScripts(config.GetProfile().GetHourlyBaseline(1.0)).Values.Single();
        boot.Should().NotContain("MEASURED_TO_SHAPED", "a factor of 1.0 leaves the script as it was");
    }
}
