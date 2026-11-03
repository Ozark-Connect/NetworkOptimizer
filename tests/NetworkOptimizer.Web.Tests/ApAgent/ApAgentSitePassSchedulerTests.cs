using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The registry's per-site scheduling. A large site mid-rollout must not hold up the others, one
/// site never runs two passes at once, and the number of sites running together is capped.
/// </summary>
public class ApAgentSitePassSchedulerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static ApAgentSitePassScheduler Scheduler(int max = 4) => new(max, NullLogger.Instance);

    private static Func<CancellationToken, Task> Blocked(TaskCompletionSource release, TaskCompletionSource? started = null)
        => async _ =>
        {
            started?.TrySetResult();
            await release.Task;
        };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ASite_mid_pass_is_skipped_rather_than_given_a_second_pass()
    {
        using var scheduler = Scheduler();
        var release = Signal();
        var runs = 0;

        scheduler.TryStart("big", async _ => { Interlocked.Increment(ref runs); await release.Task; }, CancellationToken.None).Should().BeTrue();
        scheduler.TryStart("big", _ => { Interlocked.Increment(ref runs); return Task.CompletedTask; }, CancellationToken.None).Should().BeFalse();

        release.SetResult();
        await scheduler.WhenIdleAsync().WaitAsync(Wait);
        runs.Should().Be(1);
    }

    [Fact]
    public async Task ASite_can_start_again_once_its_pass_finishes()
    {
        using var scheduler = Scheduler();

        scheduler.TryStart("site", _ => Task.CompletedTask, CancellationToken.None).Should().BeTrue();
        await scheduler.WhenIdleAsync().WaitAsync(Wait);

        scheduler.IsRunning("site").Should().BeFalse();
        scheduler.TryStart("site", _ => Task.CompletedTask, CancellationToken.None).Should().BeTrue();
        await scheduler.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task ASlow_site_does_not_hold_up_another_site()
    {
        using var scheduler = Scheduler();
        var bigRelease = Signal();
        var smallDone = Signal();

        scheduler.TryStart("big", Blocked(bigRelease), CancellationToken.None);
        scheduler.TryStart("small", _ => { smallDone.SetResult(); return Task.CompletedTask; }, CancellationToken.None);

        await smallDone.Task.WaitAsync(Wait);
        scheduler.IsRunning("big").Should().BeTrue("the big site is still mid-pass while the small one finished");

        bigRelease.SetResult();
        await scheduler.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task Sites_beyond_the_cap_wait_for_a_free_slot()
    {
        using var scheduler = Scheduler(max: 2);
        var release = Signal();
        var aStarted = Signal();
        var bStarted = Signal();
        var cStarted = Signal();

        scheduler.TryStart("a", Blocked(release, aStarted), CancellationToken.None);
        scheduler.TryStart("b", Blocked(release, bStarted), CancellationToken.None);
        await Task.WhenAll(aStarted.Task, bStarted.Task).WaitAsync(Wait);

        scheduler.TryStart("c", _ => { cStarted.SetResult(); return Task.CompletedTask; }, CancellationToken.None);
        await Task.Delay(100);
        cStarted.Task.IsCompleted.Should().BeFalse("two sites already hold both slots");
        scheduler.IsRunning("c").Should().BeTrue("a site waiting for a slot still counts as mid-pass");

        release.SetResult();
        await cStarted.Task.WaitAsync(Wait);
        await scheduler.WhenIdleAsync().WaitAsync(Wait);
    }

    [Fact]
    public async Task APass_that_throws_frees_its_site_and_its_slot()
    {
        using var scheduler = Scheduler(max: 1);

        scheduler.TryStart("broken", _ => throw new InvalidOperationException("boom"), CancellationToken.None);
        await scheduler.WhenIdleAsync().WaitAsync(Wait);

        var ran = Signal();
        scheduler.TryStart("broken", _ => { ran.SetResult(); return Task.CompletedTask; }, CancellationToken.None).Should().BeTrue();
        await ran.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Cancellation_ends_running_and_waiting_passes_quietly()
    {
        using var scheduler = Scheduler(max: 1);
        using var cts = new CancellationTokenSource();
        var started = Signal();

        scheduler.TryStart("a", async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, cts.Token);
        await started.Task.WaitAsync(Wait);
        scheduler.TryStart("b", _ => Task.CompletedTask, cts.Token);

        cts.Cancel();
        await scheduler.WhenIdleAsync().WaitAsync(Wait);

        scheduler.IsRunning("a").Should().BeFalse();
        scheduler.IsRunning("b").Should().BeFalse();
    }
}
