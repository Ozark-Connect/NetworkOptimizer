using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.SqmLearning;
using Xunit;

namespace NetworkOptimizer.Web.Tests.SqmLearning;

public class GatewayInterfaceRateProbeTests
{
    [Fact]
    public void BuildCommand_SamplesCountersAndReportsLocalTime()
    {
        var cmd = GatewayInterfaceRateProbe.BuildCommand("eth4");

        cmd.Should().Contain("/sys/class/net/eth4/statistics/rx_bytes");
        cmd.Should().Contain($"sleep {GatewayInterfaceRateProbe.WindowSeconds}");
        cmd.Should().Contain("date +%u");
        cmd.Should().Contain("date +%-H");
        // The pgrep pattern must not match the probe's own command line.
        cmd.Should().Contain("pgrep -f -- '[-]speedtest\\.sh'");
    }

    [Theory]
    [InlineData("eth4; reboot")]
    [InlineData("../etc")]
    public void BuildCommand_RejectsUnsafeInterface(string iface)
    {
        var act = () => GatewayInterfaceRateProbe.BuildCommand(iface);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_ConvertsBytesOverTheWindowToMbps()
    {
        // 375,000 bytes over 3 s = 1 Mbps exactly.
        var reading = GatewayInterfaceRateProbe.Parse("rx_bytes=375000\ntx_bytes=0\nday=1\nhour=9\npresent=1\nsqm_speedtest=0\n");

        reading.Should().NotBeNull();
        reading!.DownloadMbps.Should().BeApproximately(1.0, 0.0001);
        reading.UploadMbps.Should().Be(0);
        reading.DayOfWeek.Should().Be(1);
        reading.Hour.Should().Be(9);
        reading.InterfacePresent.Should().BeTrue();
        reading.SqmSpeedtestRunning.Should().BeFalse();
    }

    [Fact]
    public void Parse_FlagsRunningSpeedtestAndMissingInterface()
    {
        var reading = GatewayInterfaceRateProbe.Parse("rx_bytes=0\ntx_bytes=0\nday=6\nhour=23\npresent=0\nsqm_speedtest=1\n");

        reading!.InterfacePresent.Should().BeFalse();
        reading.SqmSpeedtestRunning.Should().BeTrue();
    }

    [Fact]
    public void Parse_ReadsTheGatewayMinute()
    {
        GatewayInterfaceRateProbe.Parse("rx_bytes=0\ntx_bytes=0\nday=0\nhour=18\nminute=27\n")!.Minute.Should().Be(27);
        GatewayInterfaceRateProbe.Parse("rx_bytes=0\ntx_bytes=0\nday=0\nhour=18\n")!.Minute.Should().Be(0);
    }

    [Theory]
    [InlineData(18, 27, true)]   // three minutes before the 18:30 probe
    [InlineData(18, 30, true)]   // on the minute
    [InlineData(18, 26, false)]  // four minutes out
    [InlineData(5, 58, true)]    // two minutes before the 06:00 probe
    [InlineData(12, 0, false)]
    public void ScheduledProbeImminent_UsesTheSavedProbeTimes(int hour, int minute, bool expected)
    {
        var wan = new SqmWanConfiguration { SpeedtestMorningHour = 6, SpeedtestMorningMinute = 0, SpeedtestEveningHour = 18, SpeedtestEveningMinute = 30 };

        SqmLearningExecutor.ScheduledProbeImminent(hour, minute, wan, 3).Should().Be(expected);
    }

    [Fact]
    public void Parse_TreatsAWrappedCounterAsBusy()
    {
        var reading = GatewayInterfaceRateProbe.Parse("rx_bytes=-5\ntx_bytes=10\nday=0\nhour=0\n");

        reading!.DownloadMbps.Should().Be(double.MaxValue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("rx_bytes=1\ntx_bytes=1\nday=7\nhour=0")]
    [InlineData("rx_bytes=1\ntx_bytes=1\nday=0\nhour=24")]
    public void Parse_RejectsMalformedOutput(string? output)
    {
        GatewayInterfaceRateProbe.Parse(output).Should().BeNull();
    }
}

public class SqmLearningTaskConfigTests
{
    [Fact]
    public void RoundTrips_AsCamelCaseJson()
    {
        var endsAt = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
        var cfg = new SqmLearningTaskConfig
        {
            WanNumber = 2, WanName = "Cell", WanGroup = "WAN2", ConnectionType = 5, EndsAt = endsAt, DurationSeconds = 4,
            NominalDownloadMbps = 200, NominalUploadMbps = 30, LinkSpeedOverrideMbps = 1000, RateProportionalDownloadBurst = true,
        };

        var json = cfg.ToJson();
        json.Should().Contain("\"wanNumber\":2").And.Contain("\"wanGroup\":\"WAN2\"").And.Contain("\"endsAt\"");

        var back = SqmLearningTaskConfig.Parse(json)!;
        back.WanNumber.Should().Be(2);
        back.WanName.Should().Be("Cell");
        back.WanGroup.Should().Be("WAN2");
        back.ConnectionType.Should().Be(5);
        back.EndsAt.Should().Be(endsAt);
        back.EndsAt.Kind.Should().Be(DateTimeKind.Utc);
        back.DurationSeconds.Should().Be(4);
        back.NominalDownloadMbps.Should().Be(200);
        back.NominalUploadMbps.Should().Be(30);
        back.LinkSpeedOverrideMbps.Should().Be(1000);
        back.RateProportionalDownloadBurst.Should().BeTrue();

        var sizing = back.ToWanConfiguration("eth4");
        sizing.Interface.Should().Be("eth4");
        sizing.NominalDownloadMbps.Should().Be(200);
        SqmLearningExecutor.BuildLift(sizing).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"wanNumber\":0}")]
    public void Parse_RejectsMissingOrUnusableConfig(string? json)
    {
        SqmLearningTaskConfig.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Parse_DefaultsAMissingDuration()
    {
        SqmLearningTaskConfig.Parse("{\"wanNumber\":1,\"endsAt\":\"2026-09-14T12:00:00Z\"}")!
            .DurationSeconds.Should().Be(SqmLearningTaskConfig.DefaultDurationSeconds);
    }
}

public class SqmLearningExecutorLiftTests
{
    [Fact]
    public void BuildLift_MatchesTheDeploysProbe_AndTheBurstMode()
    {
        var wan = new SqmWanConfiguration
        {
            ConnectionType = (int)ConnectionType.CellularHome,
            NominalDownloadMbps = 200,
            NominalUploadMbps = 30,
            RateProportionalDownloadBurst = true,
        };
        var lift = SqmLearningExecutor.BuildLift(wan)!;

        // Cellular MaxDownload is 1.2x nominal (240); the speedtest probe sits 3% above that.
        lift.DownloadProbeMbps.Should().Be(247);
        lift.UploadProbeMbps.Should().Be(31);
        lift.RateProportionalDownloadBurst.Should().BeTrue();
    }

    [Fact]
    public void BuildLift_NeverExceedsTheLinkCeiling()
    {
        var lift = SqmLearningExecutor.BuildLift(new SqmWanConfiguration
        {
            ConnectionType = (int)ConnectionType.Gpon,
            NominalDownloadMbps = 990,
            NominalUploadMbps = 990,
            LinkSpeedOverrideMbps = 1000,
        })!;

        // GPON MaxDownload 1039 -> probe 1070, capped at 980; upload 1020 -> capped too.
        lift.DownloadProbeMbps.Should().Be(980);
        lift.UploadProbeMbps.Should().Be(980);
    }

    [Fact]
    public void BuildLift_KeepsUploadJustAboveNominal()
    {
        var lift = SqmLearningExecutor.BuildLift(new SqmWanConfiguration
        {
            ConnectionType = (int)ConnectionType.Dsl,
            NominalDownloadMbps = 50,
            NominalUploadMbps = 2,
        })!;

        lift.UploadProbeMbps.Should().Be(3);
    }

    [Fact]
    public void ProbeLimited_IsWithinFivePercentOfTheLift()
    {
        SqmLearningExecutor.IsProbeLimited(238, 247).Should().BeTrue();
        SqmLearningExecutor.IsProbeLimited(230, 247).Should().BeFalse();
        SqmLearningExecutor.IsProbeLimited(100, 0).Should().BeFalse();
    }

    [Fact]
    public void NextLift_RaisesByHalf_AndStopsAtTheCeiling()
    {
        SqmLearningExecutor.NextLift(240, null).Should().Be(360);
        SqmLearningExecutor.NextLift(240, 300).Should().Be(300);
    }

    [Fact]
    public void RaiseLift_TakesTheRememberedLift_NeverAboveTheCeiling()
    {
        var baseLift = new SqmShaperLift(247, 31, false);

        SqmLearningExecutor.RaiseLift(baseLift, 360, null, null).Should().Be(new SqmShaperLift(360, 31, false));
        SqmLearningExecutor.RaiseLift(baseLift, 360, 50, 300).Should().Be(new SqmShaperLift(300, 50, false));
        SqmLearningExecutor.RaiseLift(baseLift, 100, null, null).Should().Be(baseLift);
        SqmLearningExecutor.RaiseLift(null, 360, 50, 300).Should().BeNull();
    }

    [Fact]
    public void BuildLift_IsNullWithoutAConfigurationToSizeFrom()
    {
        SqmLearningExecutor.BuildLift(null).Should().BeNull();
        SqmLearningExecutor.BuildLift(new SqmWanConfiguration { NominalDownloadMbps = 0 }).Should().BeNull();
    }
}
