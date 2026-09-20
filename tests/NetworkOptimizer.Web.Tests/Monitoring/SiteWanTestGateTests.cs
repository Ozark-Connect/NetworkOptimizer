using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Scheduled WAN speed tests that share a start time take turns per site instead of refusing each other.
/// </summary>
public class SiteWanTestGateTests
{
    [Fact]
    public async Task TheFirstTestOnASiteIsNotQueued()
    {
        var gate = new SiteWanTestGate();

        var (lease, queued) = await gate.EnterAsync("default", CancellationToken.None);

        queued.Should().BeFalse();
        lease.Dispose();
    }

    [Fact]
    public async Task ASecondTestOnTheSameSiteWaitsForTheFirst()
    {
        var gate = new SiteWanTestGate();
        var (first, _) = await gate.EnterAsync("default", CancellationToken.None);

        var second = gate.EnterAsync("default", CancellationToken.None);
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();

        first.Dispose();
        var (lease, queued) = await second;

        queued.Should().BeTrue();
        lease.Dispose();
    }

    [Fact]
    public async Task AnotherSiteIsNotHeldUp()
    {
        var gate = new SiteWanTestGate();
        var (first, _) = await gate.EnterAsync("default", CancellationToken.None);

        var (other, queued) = await gate.EnterAsync("site-a", CancellationToken.None);

        queued.Should().BeFalse();
        first.Dispose();
        other.Dispose();
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheGateUsable()
    {
        var gate = new SiteWanTestGate();
        var (first, _) = await gate.EnterAsync("default", CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var waiting = gate.EnterAsync("default", cts.Token);
        cts.Cancel();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        first.Dispose();
        var (next, _) = await gate.EnterAsync("default", CancellationToken.None);
        next.Dispose();
    }

    [Fact]
    public async Task DisposingALeaseTwiceReleasesOnce()
    {
        var gate = new SiteWanTestGate();
        var (first, _) = await gate.EnterAsync("default", CancellationToken.None);
        first.Dispose();
        first.Dispose();

        var (second, _) = await gate.EnterAsync("default", CancellationToken.None);
        var third = gate.EnterAsync("default", CancellationToken.None);
        await Task.Delay(50);

        third.IsCompleted.Should().BeFalse();
        second.Dispose();
        (await third).Lease.Dispose();
    }

    [Fact]
    public async Task WaitForIdleReturnsOnceTheRunningTestFinishes()
    {
        var polls = 0;

        var idle = await ScheduleExecutorRegistration.WaitForIdleAsync(
            () => Task.FromResult(++polls < 3), CancellationToken.None,
            maxWait: TimeSpan.FromSeconds(5), poll: TimeSpan.FromMilliseconds(5));

        idle.Should().BeTrue();
    }

    [Fact]
    public async Task WaitForIdleGivesUpWhenTheTestNeverFinishes()
    {
        var idle = await ScheduleExecutorRegistration.WaitForIdleAsync(
            () => Task.FromResult(true), CancellationToken.None,
            maxWait: TimeSpan.FromMilliseconds(30), poll: TimeSpan.FromMilliseconds(5));

        idle.Should().BeFalse();
    }
}
