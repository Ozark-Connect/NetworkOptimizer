using FluentAssertions;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class SnmpFailureTrackerTests
{
    private const string DeviceA = "aa:bb:cc:dd:ee:01";
    private const string DeviceB = "aa:bb:cc:dd:ee:02";
    private const string DeviceC = "aa:bb:cc:dd:ee:03";

    [Fact]
    public void NoteFailure_CrossesThreshold_ExcludesDevice()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 3);

        tracker.NoteFailure(DeviceA).Should().BeFalse();
        tracker.NoteFailure(DeviceA).Should().BeFalse();
        tracker.NoteFailure(DeviceA).Should().BeTrue("the third failure hits the threshold");

        tracker.IsExcluded(DeviceA, out _).Should().BeTrue();
    }

    [Fact]
    public void IsFailing_TrueAtConsecutiveFailures_NoPriorSuccessNeeded()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 5);

        // A cold-start device that never polled successfully - it must still register as
        // failing (this is the whole point: a restart during an outage can self-heal).
        tracker.IsFailing(DeviceA, minConsecutiveFailures: 2).Should().BeFalse();
        tracker.NoteFailure(DeviceA);
        tracker.IsFailing(DeviceA, minConsecutiveFailures: 2).Should().BeFalse("only one failure so far");
        tracker.NoteFailure(DeviceA);
        tracker.IsFailing(DeviceA, minConsecutiveFailures: 2).Should().BeTrue("two consecutive failures");
        tracker.IsFailing(DeviceA, minConsecutiveFailures: 3).Should().BeFalse("not three yet");
    }

    [Fact]
    public void IsFailing_TrueWhileExcluded()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 2);

        tracker.NoteFailure(DeviceA);
        tracker.NoteFailure(DeviceA); // hits threshold -> excluded
        tracker.IsFailing(DeviceA).Should().BeTrue("excluded devices count as failing");
    }

    [Fact]
    public void NoteSuccess_ClearsFailing()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 5);

        tracker.NoteFailure(DeviceA);
        tracker.NoteFailure(DeviceA);
        tracker.IsFailing(DeviceA, minConsecutiveFailures: 2).Should().BeTrue();

        tracker.NoteSuccess(DeviceA);
        tracker.IsFailing(DeviceA, minConsecutiveFailures: 2).Should().BeFalse("a success resets the counter");
    }

    [Fact]
    public void Reset_ClearsFailuresAndExclusions()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 2);

        tracker.NoteFailure(DeviceA);
        tracker.NoteFailure(DeviceA);
        tracker.IsExcluded(DeviceA, out _).Should().BeTrue();

        tracker.Reset();

        tracker.IsExcluded(DeviceA, out _).Should().BeFalse("exclusions cleared");
        tracker.GetFailureCount(DeviceA).Should().Be(0, "failure counters cleared");
        tracker.IsFailing(DeviceA).Should().BeFalse();
    }
}
