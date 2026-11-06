using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The scheduled PPPoE reconnect tweak: its status from the boot script, the timer and the
/// last-run record, and the time validation in front of the gateway command.
/// </summary>
public class PerfTweaksPppoeReconnectTests
{
    private const string Id = "pppoe-reconnect";

    private static Dictionary<string, string> Deployed(string timer = "active", string time = "03:30",
        string wans = "ppp0", string last = "") => new()
    {
        ["GATEWAY_MODEL"] = "UCGF",
        ["FIRMWARE_VERSION"] = "6.0.17",
        ["PPPOE_RC_BOOT_SCRIPT"] = "exists",
        ["PPPOE_RC_TIMER"] = timer,
        ["PPPOE_RC_TIME"] = time,
        ["PPPOE_RC_TZ"] = "Europe/Berlin",
        ["PPPOE_RC_WANS"] = wans,
        ["PPPOE_RC_LAST"] = last,
    };

    private static (TweakDeploymentStatus tweak, PerfTweaksStatus status) Parse(Dictionary<string, string> sections)
    {
        var output = string.Join("\n", sections.Select(kv => $"---{kv.Key}---\n{kv.Value}"));
        var status = new PerfTweaksStatus();
        PerfTweaksDeploymentService.ParseStatusOutput(output, status, DateTime.UtcNow);
        return (status.Tweaks[Id], status);
    }

    [Fact]
    public void NotDeployed_HasNoHealthChecksAndTheDefaultTime()
    {
        var (tweak, status) = Parse(new()
        {
            ["PPPOE_RC_BOOT_SCRIPT"] = "missing",
            ["PPPOE_RC_TIMER"] = "inactive",
            ["PPPOE_RC_WANS"] = "ppp0",
        });

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().BeEmpty();
        status.PppoeReconnectTime.Should().Be("04:00");
    }

    [Fact]
    public void Deployed_IsActiveWithScheduleAndWans()
    {
        var (tweak, status) = Parse(Deployed(wans: "ppp0\nppp1"));

        tweak.IsActive.Should().BeTrue();
        status.PppoeReconnectTime.Should().Be("03:30");
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Schedule", "Daily at 03:30 (Europe/Berlin)", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("PPPoE WANs", "ppp0, ppp1", HealthCheckStatus.Ok));
    }

    [Fact]
    public void TimerNotRunning_IsAnError()
    {
        var (tweak, _) = Parse(Deployed(timer: "inactive"));

        tweak.IsActive.Should().BeFalse();
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Schedule", "Timer not running", HealthCheckStatus.Error));
    }

    [Fact]
    public void NoPppoeWan_IsAWarning()
    {
        var (tweak, _) = Parse(Deployed(wans: ""));

        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("PPPoE WANs", "None found, nothing to reconnect", HealthCheckStatus.Warning));
    }

    [Fact]
    public void InvalidTimeOnGateway_FallsBackToTheDefault()
    {
        var (_, status) = Parse(Deployed(time: "25:00"));

        status.PppoeReconnectTime.Should().Be("04:00");
    }

    [Fact]
    public void LastRun_ShowsEachWansResult()
    {
        var (tweak, _) = Parse(Deployed(wans: "ppp0\nppp1", last:
            "2026-11-07 04:00 ppp0 ok reconnected in 7 s\n" +
            "2026-11-07 04:05 ppp1 failed not back after 120 s, pppd keeps dialing"));

        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Last Run (ppp0)", "2026-11-07 04:00: reconnected in 7 s", HealthCheckStatus.Ok));
        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Last Run (ppp1)", "2026-11-07 04:05: not back after 120 s, pppd keeps dialing", HealthCheckStatus.Error));
    }

    [Theory]
    [InlineData("skipped session is down", HealthCheckStatus.Ok)]
    [InlineData("skipped session is only 12 min old", HealthCheckStatus.Ok)]
    [InlineData("skipped pppd is not started with persist and would not dial again", HealthCheckStatus.Warning)]
    public void SkippedWan_IsOkUnlessPppdCannotRedial(string result, HealthCheckStatus expected)
    {
        var (tweak, _) = Parse(Deployed(last: $"2026-11-07 04:00 ppp0 {result}"));

        tweak.HealthChecks.Should().Contain(h => h.Label == "Last Run (ppp0)" && h.Status == expected);
    }

    [Fact]
    public void NoWanAtRunTime_IsRecordedWithoutUnit()
    {
        var (tweak, _) = Parse(Deployed(last: "2026-11-07 04:00 - skipped no PPPoE WAN configured"));

        tweak.HealthChecks.Should().ContainEquivalentOf(new HealthCheckResult("Last Run", "2026-11-07 04:00: no PPPoE WAN configured", HealthCheckStatus.Ok));
    }

    [Theory]
    [InlineData("00:00", true)]
    [InlineData("04:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("4:00", false)]
    [InlineData("04:00:00", false)]
    [InlineData("04:00'; reboot; echo '", false)]
    [InlineData("04:00\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TimeValidation(string? time, bool valid)
    {
        PerfTweaksDeploymentService.IsValidPppoeReconnectTime(time).Should().Be(valid);
    }

    [Fact]
    public async Task StatusProbe_EndsWithACommandThatCannotFail()
    {
        // The probe's exit code is its last command's; before the first reconnect there is no
        // last-run record, and a failing cat would turn the whole status read into an error.
        string? command = null;
        var ssh = new Mock<IGatewaySshService>();
        ssh.Setup(s => s.RunCommandAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((c, _, _) => command = c)
            .ReturnsAsync((false, "unreachable"));
        var service = new PerfTweaksDeploymentService(Mock.Of<ILogger<PerfTweaksDeploymentService>>(), ssh.Object,
            null!, null!, null!, null!, new NetworkOptimizer.Web.Services.Firmware.RolloutSuppressionRegistry());

        await service.CheckAllStatusAsync();

        command.Should().EndWith("/data/pppoe-reconnect/last-run 2>/dev/null || true");
    }

    [Fact]
    public async Task SetTime_RejectsAnInvalidTimeWithoutTouchingTheGateway()
    {
        var ssh = new Mock<IGatewaySshService>(MockBehavior.Strict);
        var service = new PerfTweaksDeploymentService(Mock.Of<ILogger<PerfTweaksDeploymentService>>(), ssh.Object,
            null!, null!, null!, null!, new NetworkOptimizer.Web.Services.Firmware.RolloutSuppressionRegistry());

        var (success, _) = await service.SetPppoeReconnectTimeAsync("04:00; reboot");

        success.Should().BeFalse();
        ssh.VerifyNoOtherCalls();
    }
}
