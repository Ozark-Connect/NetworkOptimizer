using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Backoff, the in-flight guard, and the stagger. All three are defects if left implicit, so they
/// are pinned to the decided numbers: 30 s initial, doubling, 15 min cap, reset on success.
/// </summary>
public class ApAgentRetryPolicyTests
{
    private const string Mac = "aa:bb:cc:dd:ee:ff";
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Backoff_starts_at_thirty_seconds_doubles_and_caps_at_fifteen_minutes()
    {
        var expected = new[] { 30, 60, 120, 240, 480, 900, 900, 900 };

        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            ApAgentRetryPolicy.DelayForAttempt(attempt).TotalSeconds
                .Should().Be(expected[attempt - 1], "attempt {0}", attempt);
        }
    }

    [Fact]
    public void Backoff_saturates_rather_than_overflowing_on_a_long_dead_access_point()
    {
        ApAgentRetryPolicy.DelayForAttempt(500).Should().Be(ApAgentRetryPolicy.MaxDelay);
    }

    [Fact]
    public void RecordingFailures_walks_the_next_attempt_forward()
    {
        var policy = new ApAgentRetryPolicy();

        policy.RecordFailure(Mac, Now).Should().Be(Now.AddSeconds(30));
        policy.RecordFailure(Mac, Now).Should().Be(Now.AddSeconds(60));
        policy.RecordFailure(Mac, Now).Should().Be(Now.AddSeconds(120));
        policy.ConsecutiveFailures(Mac).Should().Be(3);
    }

    [Fact]
    public void AnAccessPointInBackoff_is_not_ready_until_its_delay_elapses()
    {
        var policy = new ApAgentRetryPolicy();
        policy.RecordFailure(Mac, Now);

        policy.IsReady(Mac, Now.AddSeconds(29)).Should().BeFalse();
        policy.IsReady(Mac, Now.AddSeconds(31)).Should().BeTrue();
    }

    [Fact]
    public void Success_resets_the_backoff()
    {
        var policy = new ApAgentRetryPolicy();
        policy.RecordFailure(Mac, Now);
        policy.RecordFailure(Mac, Now);

        policy.RecordSuccess(Mac);

        policy.ConsecutiveFailures(Mac).Should().Be(0);
        policy.IsReady(Mac, Now).Should().BeTrue();
        policy.NextAttemptAt(Mac).Should().BeNull();
        policy.RecordFailure(Mac, Now).Should().Be(Now.AddSeconds(30));
    }

    [Fact]
    public void AnUnseenAccessPoint_is_ready_immediately()
    {
        new ApAgentRetryPolicy().IsReady(Mac, Now).Should().BeTrue();
    }

    [Fact]
    public void A_second_deploy_cannot_stack_on_one_already_running()
    {
        var policy = new ApAgentRetryPolicy();

        using var first = policy.TryBeginWork(Mac);

        first.Should().NotBeNull();
        policy.TryBeginWork(Mac).Should().BeNull();
        policy.IsWorkInFlight(Mac).Should().BeTrue();
    }

    [Fact]
    public void ReleasingTheClaim_lets_the_next_deploy_start()
    {
        var policy = new ApAgentRetryPolicy();

        policy.TryBeginWork(Mac)!.Dispose();

        policy.IsWorkInFlight(Mac).Should().BeFalse();
        policy.TryBeginWork(Mac).Should().NotBeNull();
    }

    [Fact]
    public void TheGuard_is_per_access_point_not_global()
    {
        var policy = new ApAgentRetryPolicy();

        using var first = policy.TryBeginWork(Mac);
        using var second = policy.TryBeginWork("00:11:22:33:44:55");

        second.Should().NotBeNull();
    }

    [Fact]
    public void DisposingAClaimTwice_does_not_release_a_later_one()
    {
        var policy = new ApAgentRetryPolicy();

        var claim = policy.TryBeginWork(Mac)!;
        claim.Dispose();
        var second = policy.TryBeginWork(Mac);
        claim.Dispose();

        policy.IsWorkInFlight(Mac).Should().BeTrue();
        second.Should().NotBeNull();
    }

    [Fact]
    public void TheStagger_is_stable_per_access_point_and_inside_the_window()
    {
        var window = TimeSpan.FromMinutes(2);

        ApAgentRetryPolicy.StaggerOffset(Mac, window)
            .Should().Be(ApAgentRetryPolicy.StaggerOffset(Mac, window));
        ApAgentRetryPolicy.StaggerOffset(Mac, window).Should().BeLessThan(window);
    }

    [Fact]
    public void TheStagger_spreads_a_fleet_rather_than_starting_it_together()
    {
        var window = TimeSpan.FromMinutes(2);
        var macs = Enumerable.Range(0, 40).Select(i => $"00:11:22:33:44:{i:x2}").ToList();

        var slots = macs.Select(m => ApAgentRetryPolicy.StaggerOffset(m, window)).ToList();

        slots.Distinct().Count().Should().BeGreaterThan(macs.Count / 2,
            "a server restart must not fire a simultaneous transfer at every access point");
    }

    [Fact]
    public void Forgetting_an_access_point_clears_its_backoff_and_its_claim()
    {
        var policy = new ApAgentRetryPolicy();
        policy.RecordFailure(Mac, Now);
        policy.TryBeginWork(Mac);

        policy.Forget(Mac);

        policy.ConsecutiveFailures(Mac).Should().Be(0);
        policy.IsWorkInFlight(Mac).Should().BeFalse();
        policy.IsReady(Mac, Now).Should().BeTrue();
    }
}
