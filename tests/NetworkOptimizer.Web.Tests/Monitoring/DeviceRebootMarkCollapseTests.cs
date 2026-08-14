using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Endpoints;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// One boot must produce one chart mark, however many records the write path left behind for it.
/// </summary>
public class DeviceRebootMarkCollapseTests
{
    private static readonly DateTime Boot = new(2026, 7, 24, 12, 40, 27, DateTimeKind.Utc);

    private static MonitoringInfluxClient.DeviceRebootPoint Record(
        string mac, DateTime bootedAt, int classifierVersion, string category = "PowerLoss",
        string? firmwareVersion = null) =>
        new()
        {
            DeviceMac = mac,
            BootedAt = bootedAt,
            ClassifierVersion = classifierVersion,
            Category = category,
            Summary = category,
            FirmwareVersion = firmwareVersion,
        };

    [Fact]
    public void SubSecondBurstForOneBootCollapsesToOneMark()
    {
        // The shape actually on file: one restart, several records under a second apart,
        // written by different classifier versions and disagreeing on the category.
        var records = new[]
        {
            Record("aabbccddee01", Boot.AddMilliseconds(0), 4, "FirmwareUpgrade"),
            Record("aabbccddee01", Boot.AddMilliseconds(18), 7, "FirmwareUpgrade"),
            Record("aabbccddee01", Boot.AddMilliseconds(38), 6, "FirmwareUpgrade"),
            Record("aabbccddee01", Boot.AddMilliseconds(196), 0, "CommandedReboot"),
            Record("aabbccddee01", Boot.AddMilliseconds(384), 2, "CommandedReboot"),
            Record("aabbccddee01", Boot.AddMilliseconds(802), 3, "FirmwareUpgrade"),
        };

        var collapsed = DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records);

        collapsed.Should().ContainSingle();
        collapsed[0].ClassifierVersion.Should().Be(7, "the newest rules re-probed the older verdicts");
        collapsed[0].Category.Should().Be("FirmwareUpgrade");
    }

    [Fact]
    public void SeparateBootsStaySeparate()
    {
        var records = new[]
        {
            Record("aabbccddee02", Boot, 7),
            Record("aabbccddee02", Boot.AddMilliseconds(500), 7),
            Record("aabbccddee02", Boot.AddDays(1), 7),
        };

        DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records).Should().HaveCount(2);
    }

    [Fact]
    public void BootsJustOutsideToleranceAreNotMerged()
    {
        var records = new[]
        {
            Record("aabbccddee02", Boot, 7),
            Record("aabbccddee02", Boot.AddMinutes(5).AddSeconds(1), 7),
        };

        DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records).Should().HaveCount(2);
    }

    [Fact]
    public void ClusterWindowDoesNotSlideWithEachAbsorbedRecord()
    {
        // Records four minutes apart are each within tolerance of the one before, so measuring
        // from the previous record would chain all five into a single mark spanning sixteen
        // minutes. Measuring from the boot each cluster opened with closes it at five, leaving
        // the run as the separate restarts it is.
        var records = new[]
        {
            Record("aabbccddee03", Boot, 7),
            Record("aabbccddee03", Boot.AddMinutes(4), 7),
            Record("aabbccddee03", Boot.AddMinutes(8), 7),
            Record("aabbccddee03", Boot.AddMinutes(12), 7),
            Record("aabbccddee03", Boot.AddMinutes(16), 7),
        };

        DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records).Should().HaveCount(3);
    }

    [Fact]
    public void DevicesAreCollapsedIndependently()
    {
        var records = new[]
        {
            Record("aabbccddee01", Boot, 7),
            Record("aabbccddee01", Boot.AddMilliseconds(300), 7),
            Record("aabbccddee02", Boot.AddMilliseconds(100), 7),
            Record("aabbccddee02", Boot.AddMilliseconds(400), 7),
        };

        var collapsed = DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records);

        collapsed.Should().HaveCount(2);
        collapsed.Select(r => r.DeviceMac).Should().BeEquivalentTo(["aabbccddee01", "aabbccddee02"]);
    }

    [Fact]
    public void MacSpellingDifferencesDoNotSplitABoot()
    {
        var records = new[]
        {
            Record("AA:BB:CC:DD:EE:01", Boot, 6),
            Record("aabbccddee01", Boot.AddMilliseconds(200), 7),
        };

        DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records).Should().ContainSingle();
    }

    [Fact]
    public void EmptyInputYieldsNoMarks()
    {
        DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot([]).Should().BeEmpty();
    }

    /// <summary>
    /// A downgrade and the roll-forward behind it land minutes apart, well inside the cluster
    /// window. They are two restarts and each has to keep its own mark.
    /// </summary>
    [Fact]
    public void ReflashPairInsideTheWindowStaysTwoMarks()
    {
        var records = new[]
        {
            Record("aabbccddee01", Boot, 7, "FirmwareUpgrade", "8.7.11.19419"),
            Record("aabbccddee01", Boot.AddMinutes(3).AddSeconds(21), 7, "FirmwareUpgrade", "8.7.9.19401"),
        };

        var collapsed = DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records);

        collapsed.Should().HaveCount(2);
        collapsed.Select(r => r.FirmwareVersion).Should().BeEquivalentTo(["8.7.11.19419", "8.7.9.19401"]);
    }

    /// <summary>
    /// The shape on file for a single upgrade: a burst of re-probes where the oldest record predates
    /// firmware being stored at all, and the rest report the same image in either of the console's
    /// two spellings. Unknown is not a difference, and neither is a spelling.
    /// </summary>
    [Fact]
    public void BurstWithBlankAndShortFirmwareStillCollapses()
    {
        var records = new[]
        {
            Record("aabbccddee01", Boot, 0, "FirmwareUpgrade"),
            Record("aabbccddee01", Boot.AddMilliseconds(180), 6, "FirmwareUpgrade", "8.7.11"),
            Record("aabbccddee01", Boot.AddMilliseconds(374), 7, "FirmwareUpgrade", "8.7.11.19419"),
        };

        var collapsed = DeviceHealthChartEndpoints.CollapseToOneRecordPerBoot(records);

        collapsed.Should().ContainSingle();
        collapsed[0].ClassifierVersion.Should().Be(7);
    }
}
