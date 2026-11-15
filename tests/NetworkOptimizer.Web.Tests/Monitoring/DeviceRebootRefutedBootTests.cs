using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// SNMP's sysUpTime counts from the SNMP service's start, so an SNMP restart reads as a reboot until
/// the device's own /proc/uptime refutes it.
/// </summary>
public class DeviceRebootRefutedBootTests
{
    private sealed class NullBus : IAlertEventBus
    {
        public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<AlertEvent> ConsumeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class NullProbe : DeviceRebootProbe
    {
        public NullProbe() : base(null!, NullLogger<DeviceRebootProbe>.Instance) { }
    }

    private sealed class NullInflux : MonitoringInfluxClient
    {
        public NullInflux() : base(null!, null!, NullLogger<MonitoringInfluxClient>.Instance) { }
    }

    private const string Mac = "aa:bb:cc:dd:ee:ff";
    private static readonly DateTime RealBoot = new(2026, 11, 4, 19, 59, 34, DateTimeKind.Utc);
    private static readonly DateTime SnmpRestart = new(2026, 11, 14, 20, 33, 31, DateTimeKind.Utc);

    private static DeviceRebootTracker BuildTracker() => new(
        new NullProbe(), new NullInflux(),
        new DeviceRebootAlertEvaluator(new NullBus(), NullLogger<DeviceRebootAlertEvaluator>.Instance),
        NullLogger<DeviceRebootTracker>.Instance);

    [Fact]
    public void ADeviceUpFor10Days_RefutesABootTwoMinutesAgo()
    {
        var probedAt = SnmpRestart.AddMinutes(2);
        var uptime = (long)(probedAt - RealBoot).TotalSeconds;

        DeviceRebootTracker.RefutedBoot(SnmpRestart, uptime, probedAt).Should().Be(RealBoot);
    }

    [Fact]
    public void ARealReboot_IsNotRefuted()
    {
        // The SNMP service starts a little after the kernel: the claimed boot trails the real one by seconds.
        var probedAt = SnmpRestart.AddMinutes(2);
        var uptime = (long)(probedAt - SnmpRestart.AddSeconds(-40)).TotalSeconds;

        DeviceRebootTracker.RefutedBoot(SnmpRestart, uptime, probedAt).Should().BeNull();
    }

    [Fact]
    public void AfterARefutedBoot_LaterSnmpSamplesAreNotReboots_AndTheOldReasonComesBack()
    {
        var tracker = BuildTracker();
        var reboots = 0;
        tracker.DeviceRebooted += _ => reboots++;
        var powerLoss = new DeviceRebootReason(RebootCategory.PowerLoss, "Power loss", "Power was removed",
            RebootReasonSource.ConsoleRebootLog);

        // Known since the real boot, with its reason.
        tracker.RecordUptimeSample(Mac, "Gateway", DeviceType.Gateway, null,
            uptimeSeconds: (long)(SnmpRestart.AddMinutes(-1) - RealBoot).TotalSeconds, firmwareVersion: "5.1.10",
            observedAt: SnmpRestart.AddMinutes(-1));
        SetReason(tracker, RealBoot, powerLoss);

        // The SNMP service restarts: its uptime drops to 53 s.
        tracker.RecordUptimeSample(Mac, "Gateway", DeviceType.Gateway, null,
            uptimeSeconds: 53, firmwareVersion: "5.1.10", observedAt: SnmpRestart.AddSeconds(53));
        reboots.Should().Be(1, "the reset looks like a reboot until the device is asked");

        // The probe finds the device up since the real boot.
        tracker.RestoreRefutedBoot(Mac, SnmpRestart, RealBoot);

        // Every later SNMP sample still counts from the service restart.
        tracker.RecordUptimeSample(Mac, "Gateway", DeviceType.Gateway, null,
            uptimeSeconds: 653, firmwareVersion: "5.1.10", observedAt: SnmpRestart.AddSeconds(653));
        reboots.Should().Be(1);
        tracker.GetReasonForReportedUptime(Mac, 653, SnmpRestart.AddSeconds(653)).Should().Be(powerLoss);
    }

    [Fact]
    public void ARealRebootAfterARefutedOne_IsStillAReboot()
    {
        var tracker = BuildTracker();
        var reboots = 0;
        tracker.DeviceRebooted += _ => reboots++;

        tracker.RecordUptimeSample(Mac, "Gateway", DeviceType.Gateway, null,
            uptimeSeconds: 53, firmwareVersion: "5.1.10", observedAt: SnmpRestart.AddSeconds(53));
        tracker.RestoreRefutedBoot(Mac, SnmpRestart, RealBoot);

        var laterBoot = SnmpRestart.AddDays(1);
        tracker.RecordUptimeSample(Mac, "Gateway", DeviceType.Gateway, null,
            uptimeSeconds: 60, firmwareVersion: "5.1.10", observedAt: laterBoot.AddSeconds(60));

        reboots.Should().Be(1);
    }

    private static void SetReason(DeviceRebootTracker tracker, DateTime bootedAt, DeviceRebootReason reason)
    {
        var field = typeof(DeviceRebootTracker)
            .GetField("_records", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var records = (System.Collections.Concurrent.ConcurrentDictionary<string, DeviceRebootTracker.DeviceBootRecord>)field.GetValue(tracker)!;
        records[Mac.Replace(":", "").ToLowerInvariant()] = new DeviceRebootTracker.DeviceBootRecord(bootedAt, reason, "5.1.10");
    }
}
