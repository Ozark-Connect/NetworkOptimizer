using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The gate that stops a path that can never resolve from being re-analysed forever. The retry it
/// bounds is essential - an analysis taken at test time often fails only because topology has not
/// caught up - so these assert that a retry is still allowed, just not immediately and not endlessly.
/// </summary>
public class PathAnalysisRetryGateTests
{
    // Result IDs are per-test so the process-wide gate cannot carry state between them.
    private static int _nextId = 10_000;
    private static int NextId() => Interlocked.Increment(ref _nextId);

    [Fact]
    public void FirstAttempt_IsAllowedImmediately()
    {
        var id = NextId();
        PathAnalysisRetryGate.TryClaim("scope", id).Should().BeTrue(
            "the follow-up analysis after a test must not be delayed");
    }

    [Fact]
    public void SecondAttempt_IsRefusedInsideTheCooldown()
    {
        var id = NextId();
        PathAnalysisRetryGate.TryClaim("scope", id);

        PathAnalysisRetryGate.TryClaim("scope", id).Should().BeFalse(
            "a read re-arming the retry is what let an unresolvable path spin");
    }

    [Fact]
    public void RepeatedAttempts_DoNotAccumulate()
    {
        var id = NextId();
        PathAnalysisRetryGate.TryClaim("scope", id);

        // A runaway makes thousands of these; every one after the first must be refused.
        for (var i = 0; i < 1_000; i++)
            PathAnalysisRetryGate.TryClaim("scope", id).Should().BeFalse();
    }

    [Fact]
    public void OtherResults_AreUnaffected()
    {
        var claimed = NextId();
        PathAnalysisRetryGate.TryClaim("scope", claimed);

        PathAnalysisRetryGate.TryClaim("scope", NextId()).Should().BeTrue(
            "one stuck result must not stop analysis of every other test");
    }

    [Fact]
    public void Scopes_DoNotCollide()
    {
        var id = NextId();
        PathAnalysisRetryGate.TryClaim("serviceA", id);

        PathAnalysisRetryGate.TryClaim("serviceB", id).Should().BeTrue(
            "IDs come from different tables per service and would otherwise block each other");
    }

    [Fact]
    public void Forget_ReopensTheGate()
    {
        var id = NextId();
        PathAnalysisRetryGate.TryClaim("scope", id);
        PathAnalysisRetryGate.TryClaim("scope", id).Should().BeFalse();

        PathAnalysisRetryGate.Forget("scope", id);

        PathAnalysisRetryGate.TryClaim("scope", id).Should().BeTrue(
            "reassigning a WAN is a deliberate re-analysis and starts clean");
    }
}
