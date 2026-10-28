using FluentAssertions;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Which schedules a main site with no UniFi Console skips. Only the ones that need the console or
/// its gateway: the server-side WAN speed test and LAN speed tests keep running.
/// </summary>
public class ScheduleNeedsConsoleTests
{
    private static ScheduledTask Task(string type, string? config = null) =>
        new() { TaskType = type, Name = "Test", TargetConfig = config };

    [Theory]
    [InlineData("audit")]
    [InlineData(ScheduleService.SqmLearningTaskType)]
    public void ConsoleTasks_NeedTheConsole(string type)
    {
        ScheduleExecutorRegistration.NeedsConsole(Task(type)).Should().BeTrue();
    }

    [Fact]
    public void GatewayWanTest_NeedsTheConsole()
    {
        ScheduleExecutorRegistration.NeedsConsole(Task("wan_speedtest", """{"testType":"gateway"}""")).Should().BeTrue();
    }

    [Fact]
    public void WanTestWithNoConfig_IsTheGatewayTest()
    {
        ScheduleExecutorRegistration.NeedsConsole(Task("wan_speedtest")).Should().BeTrue();
    }

    [Fact]
    public void UnreadableWanConfig_IsSkipped()
    {
        ScheduleExecutorRegistration.NeedsConsole(Task("wan_speedtest", "{not json")).Should().BeTrue();
    }

    [Fact]
    public void ServerWanTest_RunsWithoutTheConsole()
    {
        ScheduleExecutorRegistration.NeedsConsole(Task("wan_speedtest", """{"testType":"server"}""")).Should().BeFalse();
    }

    [Fact]
    public void LanSpeedTest_RunsWithoutTheConsole()
    {
        ScheduleExecutorRegistration.NeedsConsole(Task("lan_speedtest")).Should().BeFalse();
    }
}
