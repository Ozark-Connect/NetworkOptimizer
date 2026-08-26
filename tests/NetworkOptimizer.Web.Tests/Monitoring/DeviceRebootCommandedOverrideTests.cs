using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Tests that a UniFi Network restart event overrides an unexpected SSH probe result when
/// the device crashed during a commanded shutdown sequence.
/// </summary>
public class DeviceRebootCommandedOverrideTests
{
    private sealed class CapturingBus : IAlertEventBus
    {
        public List<AlertEvent> Published { get; } = [];

        public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken ct = default)
        {
            Published.Add(alertEvent);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<AlertEvent> ConsumeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NullProbe : DeviceRebootProbe
    {
        public NullProbe() : base(null!, null!, NullLogger<DeviceRebootProbe>.Instance) { }
    }

    private sealed class NullInflux : MonitoringInfluxClient
    {
        public NullInflux() : base(null!, null!, NullLogger<MonitoringInfluxClient>.Instance) { }
    }

    private static readonly DateTime Now = new(2026, 8, 25, 18, 0, 0, DateTimeKind.Utc);
    private const string Mac = "aa:bb:cc:dd:ee:ff";

    private static DeviceRebootTracker BuildTracker(CapturingBus? bus = null)
    {
        bus ??= new CapturingBus();
        var alertEvaluator = new DeviceRebootAlertEvaluator(
            bus, NullLogger<DeviceRebootAlertEvaluator>.Instance);
        return new DeviceRebootTracker(
            new NullProbe(), new NullInflux(), alertEvaluator,
            NullLogger<DeviceRebootTracker>.Instance);
    }

    [Fact]
    public async Task UniFiRestartEvent_OverridesAbruptStop()
    {
        var bus = new CapturingBus();
        var tracker = BuildTracker(bus);

        // Simulate: device booted, probe resolved AbruptStop, then UniFi event arrives
        tracker.RecordUptimeSample(Mac, "AP 2", DeviceType.AccessPoint, "192.0.2.22",
            uptimeSeconds: 120, firmwareVersion: "7.1.2", observedAt: Now);

        // Manually set the reason as if the probe resolved AbruptStop
        var bootedAt = Now.AddSeconds(-120);
        var abruptStop = new DeviceRebootReason(
            RebootCategory.AbruptStop, "Unexpected stop",
            "The device stopped without shutting down, so it either lost power or hung",
            RebootReasonSource.PstoreConsole);

        // Access the internal state to simulate probe completion
        var recordField = typeof(DeviceRebootTracker)
            .GetField("_records", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var records = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceRebootTracker.DeviceBootRecord>)recordField.GetValue(tracker)!;
        records[Mac.Replace(":", "").ToLowerInvariant()] =
            new DeviceRebootTracker.DeviceBootRecord(bootedAt, abruptStop, "7.1.2");

        // Now a UniFi restart event arrives
        await tracker.ApplyUniFiEventFallbackAsync(Mac, "EVT_AP_Restarted");

        // The reason should be overridden to CommandedReboot
        var reason = tracker.GetReasonForReportedUptime(Mac, 120, Now);
        Assert.NotNull(reason);
        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.Equal("Restarted", reason.Summary);
        Assert.Contains("not clean", reason.Detail);
    }

    [Fact]
    public async Task UniFiRestartUnknown_DoesNotOverride()
    {
        var tracker = BuildTracker();

        tracker.RecordUptimeSample(Mac, "AP 2", DeviceType.AccessPoint, "192.0.2.22",
            uptimeSeconds: 120, firmwareVersion: "7.1.2", observedAt: Now);

        var bootedAt = Now.AddSeconds(-120);
        var abruptStop = new DeviceRebootReason(
            RebootCategory.AbruptStop, "Unexpected stop", "evidence",
            RebootReasonSource.PstoreConsole);

        var recordField = typeof(DeviceRebootTracker)
            .GetField("_records", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var records = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceRebootTracker.DeviceBootRecord>)recordField.GetValue(tracker)!;
        records[Mac.Replace(":", "").ToLowerInvariant()] =
            new DeviceRebootTracker.DeviceBootRecord(bootedAt, abruptStop, "7.1.2");

        // EVT_AP_RestartedUnknown is NOT a commanded restart - it's UniFi saying "I don't know"
        await tracker.ApplyUniFiEventFallbackAsync(Mac, "EVT_AP_RestartedUnknown");

        var reason = tracker.GetReasonForReportedUptime(Mac, 120, Now);
        Assert.NotNull(reason);
        Assert.Equal(RebootCategory.AbruptStop, reason!.Category);
    }

    [Fact]
    public async Task UniFiRestartEvent_FallbackWhenNoProbeResult()
    {
        var tracker = BuildTracker();

        tracker.RecordUptimeSample(Mac, "Switch 1", DeviceType.Switch, "192.0.2.30",
            uptimeSeconds: 60, firmwareVersion: "7.5.6", observedAt: Now);

        // No probe result yet - the event should be applied as the fallback reason
        await tracker.ApplyUniFiEventFallbackAsync(Mac, "EVT_SW_Restarted");

        var reason = tracker.GetReasonForReportedUptime(Mac, 60, Now);
        Assert.NotNull(reason);
        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.Equal(RebootReasonSource.UniFiEvent, reason.Source);
    }

    [Fact]
    public async Task UniFiRestartEvent_DoesNotOverrideConclusive_NonUnexpected()
    {
        var tracker = BuildTracker();

        tracker.RecordUptimeSample(Mac, "Switch 1", DeviceType.Switch, "192.0.2.30",
            uptimeSeconds: 60, firmwareVersion: "7.5.6", observedAt: Now);

        var bootedAt = Now.AddSeconds(-60);
        var commanded = new DeviceRebootReason(
            RebootCategory.CommandedReboot, "Restarted",
            "The device shut down cleanly",
            RebootReasonSource.PstoreConsole);

        var recordField = typeof(DeviceRebootTracker)
            .GetField("_records", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var records = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceRebootTracker.DeviceBootRecord>)recordField.GetValue(tracker)!;
        records[Mac.Replace(":", "").ToLowerInvariant()] =
            new DeviceRebootTracker.DeviceBootRecord(bootedAt, commanded, "7.5.6");

        // An already-correct CommandedReboot should not be touched
        await tracker.ApplyUniFiEventFallbackAsync(Mac, "EVT_SW_Restarted");

        var reason = tracker.GetReasonForReportedUptime(Mac, 60, Now);
        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.Equal(RebootReasonSource.PstoreConsole, reason.Source);
    }

    [Fact]
    public async Task UniFiRestartEvent_OverridesWithAdminName()
    {
        var tracker = BuildTracker();

        tracker.RecordUptimeSample(Mac, "AP 1", DeviceType.AccessPoint, "192.0.2.22",
            uptimeSeconds: 90, firmwareVersion: "7.1.2", observedAt: Now);

        var bootedAt = Now.AddSeconds(-90);
        var abruptStop = new DeviceRebootReason(
            RebootCategory.AbruptStop, "Unexpected stop", "evidence",
            RebootReasonSource.PstoreConsole);

        var recordField = typeof(DeviceRebootTracker)
            .GetField("_records", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var records = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceRebootTracker.DeviceBootRecord>)recordField.GetValue(tracker)!;
        records[Mac.Replace(":", "").ToLowerInvariant()] =
            new DeviceRebootTracker.DeviceBootRecord(bootedAt, abruptStop, "7.1.2");

        await tracker.ApplyUniFiEventFallbackAsync(Mac, "EVT_AP_Restarted", adminName: "TJ");

        var reason = tracker.GetReasonForReportedUptime(Mac, 90, Now);
        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.Contains("Restarted by TJ", reason.Detail);
        Assert.Contains("not clean", reason.Detail);
    }
}
