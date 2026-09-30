using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;
using static NetworkOptimizer.Web.Tests.Firmware.RolloutFixtures;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// What the executor does on its own is audited with Network Optimizer as the actor. A user's own
/// Start Now is audited by the service gate, so the executor must not record it a second time.
/// </summary>
public class RolloutAuditTests
{
    private static IEnumerable<AuditEvent> System(RolloutHarness harness) =>
        harness.AuditLog.Events.Where(e => e.ActorAuthMethod == "system");

    [Fact]
    public async Task ScheduledStart_IsAuditedOnce()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))), RolloutHarness.Start, Step(ApMac));

        await harness.TickAsync();
        await harness.Orchestrator.StartDueScheduledPlansAsync();

        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.Running);
        var started = System(harness).Should().ContainSingle().Subject;
        started.Action.Should().Be(AuditActions.FirmwareRolloutStarted);
        started.ActorName.Should().Be("Network Optimizer");
        started.TargetId.Should().Be(plan.Id.ToString());
    }

    [Fact]
    public async Task HealthGatePostpone_IsAuditedWithTheReason()
    {
        using var harness = new RolloutHarness();
        harness.Health.Verdict = RolloutHealthVerdict.Blocked("a critical alert is open (WAN Outage)");
        await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))), RolloutHarness.Start, Step(ApMac));

        await harness.TickAsync();

        var postponed = System(harness).Should().ContainSingle().Subject;
        postponed.Action.Should().Be(AuditActions.FirmwareRolloutPostponed);
        postponed.DetailsJson.Should().Contain("WAN Outage");
    }

    [Fact]
    public async Task ManualStart_IsNotAuditedByTheExecutor()
    {
        using var harness = new RolloutHarness();
        var plan = await harness.SeedScheduledPlanAsync(
            Document(Wave(1, PlanStep(ApMac))), RolloutHarness.Start.AddDays(1), Step(ApMac));

        (await harness.Orchestrator.StartNowAsync(plan.Id, overrideHealthGate: true)).Should().BeTrue();

        System(harness).Should().BeEmpty();
    }
}
