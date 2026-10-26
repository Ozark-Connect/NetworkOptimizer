using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.HealthChecks;
using Xunit;
using static NetworkOptimizer.Web.Tests.Monitoring.OpenAlertTestScope;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Restart seeding for the evaluators too wired-in to drive end to end here: the stored alert's
/// subject has to be the key the evaluator looks it up by, or the seed silently finds nothing.
/// </summary>
public class RestartSeedTests
{
    [Fact]
    public async Task HealthCheck_FailingBeforeRestart_StartsTripped()
    {
        var failedAt = TimeSpan.FromMinutes(20);
        var open = await new OpenAlertSeed(With(Alert(HealthCheckAlertTypes.Failed, "aa:bb:cc:dd:ee:ff", failedAt,
                context: ("check_id", "7"))),
            "default", [HealthCheckAlertTypes.Failed], HealthCheckRunner.SeedSubject, NullLogger.Instance).OpenAsync();

        var state = HealthCheckRunner.NewCheckState(open, 7);

        Assert.True(state.Status.Tripped);
        Assert.NotNull(state.LastFailedAlertAt);
        Assert.False(HealthCheckRunner.NewCheckState(open, 7).Status.Tripped);
    }

    [Fact]
    public async Task HealthCheck_RecoveredBeforeRestart_StartsClear()
    {
        var open = await new OpenAlertSeed(With(
                Alert(HealthCheckAlertTypes.Failed, "aa:bb:cc:dd:ee:ff", TimeSpan.FromMinutes(20), context: ("check_id", "7")),
                Alert(HealthCheckAlertTypes.Recovered, "aa:bb:cc:dd:ee:ff", TimeSpan.FromMinutes(10), context: ("check_id", "7"))),
            "default", [HealthCheckAlertTypes.Failed], HealthCheckRunner.SeedSubject, NullLogger.Instance).OpenAsync();

        Assert.False(HealthCheckRunner.NewCheckState(open, 7).Status.Tripped);
    }

    [Fact]
    public async Task Agent_OfflineBeforeRestart_IsFoundByItsId()
    {
        var open = await new OpenAlertSeed(With(
                Alert("agent.offline", null, TimeSpan.FromMinutes(20), context: ("agent_id", "5")),
                Alert("agent.reconnected", null, TimeSpan.FromMinutes(10), context: ("agent_id", "6"))),
            "default", ["agent.offline"], AgentConnectionAlertMonitor.SeedSubject, NullLogger.Instance).OpenAsync();

        Assert.NotNull(open.Take("agent.offline", "5"));
        Assert.Null(open.Take("agent.offline", "6"));
    }

    [Theory]
    [InlineData(3, 1, DeviceReach.Answered)]
    [InlineData(3, 0, DeviceReach.Silent)]
    [InlineData(0, 0, DeviceReach.CouldNotRun)]
    public void ReachabilityProbe_ClassifiesThePing(int sent, int received, DeviceReach expected)
    {
        var result = new NetworkOptimizer.Monitoring.Probes.PingProbeResult
        {
            Target = new NetworkOptimizer.Monitoring.Probes.ProbeTarget("192.0.2.10", NetworkOptimizer.Core.Enums.ProbeMode.Icmp),
            Vantage = NetworkOptimizer.Monitoring.Probes.ProbeVantage.Server,
            Sent = sent,
            Received = received,
            Timestamp = DateTime.UtcNow,
        };

        Assert.Equal(expected, DeviceReachabilityProbe.Classify(result));
    }

    /// <summary>Agent alerts from before agent_id was recorded name no subject, so they seed nothing rather than guess by name.</summary>
    [Fact]
    public async Task Agent_OfflineWithoutAnId_SeedsNothing()
    {
        var open = await new OpenAlertSeed(With(Alert("agent.offline", null, TimeSpan.FromMinutes(20))),
            "default", ["agent.offline"], AgentConnectionAlertMonitor.SeedSubject, NullLogger.Instance).OpenAsync();

        Assert.Empty(open.All);
    }
}
