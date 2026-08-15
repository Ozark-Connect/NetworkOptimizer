using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The PDF export renders a persisted report and computes nothing, so what is worth proving is that
/// every section survives QuestPDF's layout pass for the report shapes a rollout can produce - a
/// full one, and the bare one a rollout that upgraded nothing leaves behind.
/// </summary>
public class RolloutReportPdfGeneratorTests
{
    private static readonly DateTime Started = new(2026, 8, 14, 3, 0, 0, DateTimeKind.Utc);

    private static RolloutReport MinimalReport() => new()
    {
        GeneratedAt = Started.AddHours(25),
        StartedAt = Started,
        CompletedAt = Started.AddHours(1),
        TotalSeconds = 3600,
    };

    private static RolloutReport FullReport()
    {
        var report = MinimalReport();
        report.DevicesUpgraded = 2;
        report.DevicesFailed = 1;
        report.DevicesRolledBack = 1;
        report.DevicesSkipped = 1;
        report.SiteCpuBeforeMean = 12.5;
        report.SiteCpuAfterMean = 14.25;
        report.SiteMemBeforeMean = 51;
        report.SiteMemAfterMean = 49.5;
        report.UniFiNetworkUpdateOutcome = "updated";
        report.UniFiOsUpdateOutcome = "stuck";
        report.Rows.AddRange(
        [
            new RolloutReportRow
            {
                Mac = "aa:bb:cc:dd:ee:01", Name = "AP 1", Model = "U6PRO", DeviceType = "uap",
                FromVersion = "6.6.55.1234", ToVersion = "7.0.11.5678",
                ChangelogUrl = "https://fw-update.ui.com/api/changelog/1",
                UpgradedAt = Started.AddMinutes(6), DowntimeSeconds = 245,
                Outcome = RolloutOutcomes.Upgraded,
                CpuBeforeMean = 10, CpuAfterMean = 11, MemBeforeMean = 42, MemAfterMean = 43,
            },
            new RolloutReportRow
            {
                Mac = "aa:bb:cc:dd:ee:02", Name = "AP 2", Model = "U6PRO", DeviceType = "uap",
                FromVersion = "6.6.55.1234", ToVersion = "7.0.11.5678",
                UpgradedAt = Started.AddMinutes(16), DowntimeSeconds = 260,
                Outcome = RolloutOutcomes.RegressionFlagged,
                CpuBeforeMean = 15, CpuAfterMean = 30, MemBeforeMean = 60, MemAfterMean = 56,
            },
            new RolloutReportRow
            {
                Mac = "aa:bb:cc:dd:ee:03", Name = "Switch 1", Model = "USL24", DeviceType = "usw",
                FromVersion = "6.6.55.1234", ToVersion = "7.0.11.5678",
                Outcome = RolloutOutcomes.Failed,
                Issue = "The device has been offline for over 15 minutes and has not come back.",
            },
            new RolloutReportRow
            {
                Mac = "aa:bb:cc:dd:ee:04", Name = "Gateway", Model = "UCGMAX", DeviceType = "ugw",
                Outcome = RolloutOutcomes.Skipped,
            },
        ]);
        report.Issues.Add("Switch 1 (USW24): The device has been offline for over 15 minutes and has not come back.");
        report.Notes.Add("2 U6PRO devices are waiting for 7.0.12 to age 7 days; it was published 2 days ago.");
        return report;
    }

    [Fact]
    public void GenerateReportBytes_RendersAFullReport()
    {
        var pdf = new RolloutReportPdfGenerator().GenerateReportBytes(FullReport(), "Test Site");

        pdf.Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public void GenerateReportBytes_RendersAReportWithNoDevicesAndNoSiteName()
    {
        var pdf = new RolloutReportPdfGenerator().GenerateReportBytes(MinimalReport());

        pdf.Length.Should().BeGreaterThan(1000);
    }
}
