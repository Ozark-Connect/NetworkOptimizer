using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.SqmLearning;
using Xunit;

namespace NetworkOptimizer.Web.Tests.SqmLearning;

public class SqmSpeedTestClearanceTests
{
    private static readonly SqmWanConfiguration[] Wans =
    {
        new() { WanNumber = 1, Name = "Fiber", Enabled = true, SpeedtestMorningHour = 6, SpeedtestMorningMinute = 0, SpeedtestEveningHour = 18, SpeedtestEveningMinute = 30 },
    };

    private static SqmSpeedTestClearance.Reading At(string time, bool running = false) =>
        new(TimeSpan.Parse(time), running);

    [Theory]
    [InlineData("06:00:00", true)]   // due now, the script may not be visible yet
    [InlineData("06:00:09", true)]   // inside the spawn grace
    [InlineData("06:00:10", false)]  // past the grace: only a running script holds now
    [InlineData("05:59:01", true)]   // 59 s ahead: a 45 s test would still be running
    [InlineData("05:59:00", false)]  // 60 s ahead: a 45 s test ends with 15 s to spare
    [InlineData("05:55:00", false)]
    [InlineData("18:29:30", true)]
    [InlineData("18:31:00", false)]
    public void HoldReason_HoldsAroundTheScheduledTimes(string time, bool held)
    {
        SqmSpeedTestClearance.HoldReason(At(time), Wans).Should().Match(r => (r != null) == held);
    }

    [Fact]
    public void HoldReason_HoldsWhileTheScriptRuns()
    {
        SqmSpeedTestClearance.HoldReason(At("12:00:00", running: true), Wans)
            .Should().Contain("running");
    }

    [Fact]
    public void HoldReason_WrapsMidnight()
    {
        var wans = new[] { new SqmWanConfiguration { Enabled = true, SpeedtestMorningHour = 0, SpeedtestMorningMinute = 0, SpeedtestEveningHour = 12 } };

        SqmSpeedTestClearance.HoldReason(At("23:59:30"), wans).Should().NotBeNull();
        SqmSpeedTestClearance.HoldReason(At("00:00:05"), wans).Should().NotBeNull();
    }

    [Fact]
    public void HoldReason_IgnoresDisabledWans()
    {
        var wans = new[] { new SqmWanConfiguration { Enabled = false, SpeedtestMorningHour = 6, SpeedtestMorningMinute = 0 } };

        SqmSpeedTestClearance.HoldReason(At("06:00:00"), wans).Should().BeNull();
    }

    [Fact]
    public void Parse_ReadsTimeAndRunningFlag()
    {
        var reading = SqmSpeedTestClearance.Parse("time=05:59:42\nsqm_speedtest=1\n");

        reading.Should().NotBeNull();
        reading!.TimeOfDay.Should().Be(new TimeSpan(5, 59, 42));
        reading.SqmSpeedtestRunning.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sqm_speedtest=0")]
    [InlineData("time=garbage\nsqm_speedtest=0")]
    [InlineData("Permission denied")]
    public void Parse_RejectsOtherOutput(string? output)
    {
        SqmSpeedTestClearance.Parse(output).Should().BeNull();
    }

    [Fact]
    public void Command_UsesTheSharedRunningCheck()
    {
        SqmSpeedTestClearance.Command.Should().Contain(GatewayInterfaceRateProbe.SqmSpeedtestRunningShell);
        SqmSpeedTestClearance.Command.Should().Contain("date +%H:%M:%S");
    }
}
