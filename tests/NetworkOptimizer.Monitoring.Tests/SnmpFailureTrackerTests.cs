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
    public void NoteSuccess_MarksDeviceHealthy()
    {
        var tracker = new SnmpFailureTracker();

        tracker.HealthyCount.Should().Be(0);
        tracker.NoteSuccess(DeviceA);
        tracker.NoteSuccess(DeviceB);
        tracker.NoteSuccess(DeviceA); // idempotent per device

        tracker.HealthyCount.Should().Be(2);
    }

    [Fact]
    public void ExcludedHealthyCount_CountsOnlyPreviouslyHealthyDevices()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 2);

        // A and B polled successfully before; C never did (e.g. a device that
        // does not speak SNMP), so its later failures must not count.
        tracker.NoteSuccess(DeviceA);
        tracker.NoteSuccess(DeviceB);

        foreach (var device in new[] { DeviceA, DeviceB, DeviceC })
        {
            tracker.NoteFailure(device);
            tracker.NoteFailure(device);
        }

        tracker.ExcludedHealthyCount().Should().Be(2, "only A and B were healthy before failing");
    }

    [Fact]
    public void Reset_ClearsFailuresAndExclusions_RetainsHealthyBaseline()
    {
        var tracker = new SnmpFailureTracker(failureThreshold: 2);

        tracker.NoteSuccess(DeviceA);
        tracker.NoteSuccess(DeviceB);
        tracker.NoteFailure(DeviceA);
        tracker.NoteFailure(DeviceA);
        tracker.IsExcluded(DeviceA, out _).Should().BeTrue();

        tracker.Reset();

        tracker.IsExcluded(DeviceA, out _).Should().BeFalse("exclusions cleared");
        tracker.GetFailureCount(DeviceA).Should().Be(0, "failure counters cleared");
        tracker.HealthyCount.Should().Be(2, "seen-healthy baseline is retained");
        tracker.ExcludedHealthyCount().Should().Be(0);
    }
}
