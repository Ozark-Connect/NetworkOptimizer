using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The discrimination table. "Not answering" is not one condition, and most of its causes must NOT
/// trigger a redeploy, so every row is pinned here rather than left to reading.
/// </summary>
public class ApAgentHealthClassifierTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentHealthPayload Health(
        int binaryVersion = 3, TimeSpan? probeAge = null, bool degraded = false, params string[] unavailable)
        => new("1.0.0", binaryVersion, Now - (probeAge ?? TimeSpan.FromMinutes(1)), Now, degraded, unavailable);

    [Fact]
    public void ConnectionRefused_redeploys()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Refused, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.NotListening);
        result.Action.Should().Be(ApAgentAction.Redeploy);
    }

    [Fact]
    public void Timeout_surfaces_the_path_problem_and_never_redeploys()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.TimedOut, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Filtered);
        result.Action.Should().Be(ApAgentAction.SurfacePathProblem);
    }

    [Fact]
    public void NoRoute_surfaces_the_path_problem()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Unreachable, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Filtered);
        result.Action.Should().Be(ApAgentAction.SurfacePathProblem);
    }

    [Fact]
    public void Unauthorized_repushes_the_config_not_the_binary()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 401, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Unauthorized);
        result.Action.Should().Be(ApAgentAction.RepushConfig);
    }

    [Fact]
    public void StaleTimestamps_restart_in_place_without_re_transferring()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200,
                Health: Health(probeAge: TimeSpan.FromMinutes(45)),
                ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Wedged);
        result.Action.Should().Be(ApAgentAction.RestartInPlace);
    }

    [Fact]
    public void OldVersion_is_an_upgrade_not_an_emergency()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200,
                Health: Health(binaryVersion: 2),
                ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.OutOfDate);
        result.Action.Should().Be(ApAgentAction.Upgrade);
    }

    [Fact]
    public void CurrentAndFresh_is_healthy_with_nothing_to_do()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200, Health: Health(), ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Healthy);
        result.Action.Should().Be(ApAgentAction.None);
    }

    [Fact]
    public void NewerThanTheServer_is_still_healthy()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200,
                Health: Health(binaryVersion: 9), ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Healthy);
        result.Action.Should().Be(ApAgentAction.None);
    }

    [Fact]
    public void OfflineAccessPoint_waits_and_burns_no_ssh_attempts()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Refused, DeviceOnline: false, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.ApOffline);
        result.Action.Should().Be(ApAgentAction.Wait);
    }

    [Fact]
    public void UnsupportedHardware_outranks_everything_else()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Refused, DeviceOnline: false, SupportedArchitecture: false));

        result.State.Should().Be(ApAgentState.Unsupported);
        result.Action.Should().Be(ApAgentAction.None);
    }

    [Fact]
    public void UnexplainedFailure_does_not_redeploy_on_a_guess()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Unknown, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Unhealthy);
        result.Action.Should().Be(ApAgentAction.None);
    }

    [Fact]
    public void ServerError_is_unhealthy_rather_than_a_redeploy()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 500, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Unhealthy);
        result.Action.Should().Be(ApAgentAction.None);
    }

    [Fact]
    public void TwoHundredWithAnUnreadableBody_is_unhealthy_not_healthy()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200, Health: null, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Unhealthy);
        result.Action.Should().Be(ApAgentAction.None);
    }

    [Fact]
    public void AnAgentThatHasNeverProbed_is_not_called_wedged()
    {
        var payload = new ApAgentHealthPayload("1.0.0", 3, default, Now, false, Array.Empty<string>());

        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200, Health: payload, ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Healthy);
    }

    [Fact]
    public void DegradedProbes_are_reported_but_are_not_a_fault()
    {
        var result = ApAgentHealthClassifier.Classify(
            new ApAgentObservation(ApAgentReach.Answered, 200,
                Health: Health(degraded: true, unavailable: "stahtd"),
                ExpectedBinaryVersion: 3));

        result.State.Should().Be(ApAgentState.Healthy);
        result.Action.Should().Be(ApAgentAction.None);
        result.Detail.Should().Contain("stahtd");
    }

    [Theory]
    [InlineData("Connection refused", ApAgentReach.Refused)]
    [InlineData("connect timed out", ApAgentReach.TimedOut)]
    [InlineData("No route to host", ApAgentReach.Unreachable)]
    [InlineData("Network is unreachable", ApAgentReach.Unreachable)]
    [InlineData("something else entirely", ApAgentReach.Unknown)]
    [InlineData(null, ApAgentReach.Unknown)]
    public void TunnelFailureReasons_map_to_the_same_distinction(string? reason, ApAgentReach expected)
    {
        ApAgentHealthClassifier.ReachFromTunnelFailure(reason).Should().Be(expected);
    }
}
