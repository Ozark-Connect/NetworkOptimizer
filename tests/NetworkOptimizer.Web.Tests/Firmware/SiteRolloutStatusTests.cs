using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>Which rollout a site surfaces on the Dashboard Sites panel, and how it reads.</summary>
public class SiteRolloutStatusTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private static FirmwareRolloutPlan Plan(int id, FirmwareRolloutStatus status, DateTime? scheduled = null,
        DateTime? started = null, DateTime? completed = null, string? reportJson = null) => new()
        {
            Id = id,
            Status = status,
            ScheduledStartAt = scheduled,
            StartedAt = started,
            CompletedAt = completed,
            ReportJson = reportJson,
        };

    private static string Report(DateTime generatedAt, int upgraded, int failed = 0) =>
        JsonSerializer.Serialize(new RolloutReport
        {
            GeneratedAt = generatedAt,
            DevicesFailed = failed,
            Rows = Enumerable.Range(0, upgraded).Select(_ => new RolloutReportRow { Outcome = RolloutOutcomes.Upgraded }).ToList(),
        });

    [Theory]
    [InlineData(FirmwareRolloutStatus.Scheduled, SiteRolloutStage.Scheduled)]
    [InlineData(FirmwareRolloutStatus.Announced, SiteRolloutStage.Scheduled)]
    [InlineData(FirmwareRolloutStatus.Running, SiteRolloutStage.Running)]
    [InlineData(FirmwareRolloutStatus.Paused, SiteRolloutStage.Paused)]
    [InlineData(FirmwareRolloutStatus.SoakWait, SiteRolloutStage.Soaking)]
    public void ActivePlan_MapsToItsStage(FirmwareRolloutStatus status, SiteRolloutStage expected)
    {
        var result = SiteRolloutStatus.From(Plan(7, status, Now.AddHours(5), Now.AddHours(-1), Now.AddMinutes(-30)), [], Now);

        result!.Stage.Should().Be(expected);
        result.Href.Should().Be("/firmware-rollout");
    }

    [Fact]
    public void DraftPlan_IsNotSurfaced()
    {
        SiteRolloutStatus.From(Plan(1, FirmwareRolloutStatus.Draft), [], Now).Should().BeNull();
    }

    [Fact]
    public void ReportWithin24Hours_IsSurfacedAndLinksToItsReport()
    {
        var plans = new[] { Plan(9, FirmwareRolloutStatus.Reported, completed: Now.AddHours(-30), reportJson: Report(Now.AddHours(-3), 4)) };

        var result = SiteRolloutStatus.From(null, plans, Now);

        result!.Stage.Should().Be(SiteRolloutStage.Completed);
        result.At.Should().Be(Now.AddHours(-3));
        result.Href.Should().Be("/firmware-rollout?report=9");
    }

    [Fact]
    public void ReportOlderThan24Hours_IsNotSurfaced()
    {
        var plans = new[] { Plan(9, FirmwareRolloutStatus.Reported, reportJson: Report(Now.AddHours(-25), 4)) };

        SiteRolloutStatus.From(null, plans, Now).Should().BeNull();
    }

    [Fact]
    public void AbortedPlan_IsNotSurfaced()
    {
        var plans = new[] { Plan(3, FirmwareRolloutStatus.Aborted, completed: Now.AddHours(-1)) };

        SiteRolloutStatus.From(null, plans, Now).Should().BeNull();
    }

    [Theory]
    [InlineData(3, 1, SiteRolloutStage.Partial)]
    [InlineData(0, 2, SiteRolloutStage.Failed)]
    public void ReportWithFailures_ReadsPartialOrFailed(int upgraded, int failed, SiteRolloutStage expected)
    {
        var plans = new[] { Plan(4, FirmwareRolloutStatus.Reported, reportJson: Report(Now.AddHours(-2), upgraded, failed)) };

        SiteRolloutStatus.From(null, plans, Now)!.Stage.Should().Be(expected);
    }

    [Theory]
    [InlineData(SiteRolloutStage.Running, 0, true)]
    [InlineData(SiteRolloutStage.Paused, 0, true)]
    [InlineData(SiteRolloutStage.Soaking, 0, true)]
    [InlineData(SiteRolloutStage.Scheduled, 23, true)]
    [InlineData(SiteRolloutStage.Scheduled, 25, false)]
    [InlineData(SiteRolloutStage.Completed, -2, false)]
    [InlineData(SiteRolloutStage.Failed, -2, false)]
    public void NeedsAttention_UnderWayOrStartingWithinADay(SiteRolloutStage stage, int hoursFromNow, bool expected)
    {
        new SiteRolloutStatus(1, stage, Now.AddHours(hoursFromNow)).NeedsAttention(Now).Should().Be(expected);
    }

    [Fact]
    public void Describe_SaysWhenAScheduledRolloutStarts()
    {
        var result = SiteRolloutStatus.From(Plan(1, FirmwareRolloutStatus.Scheduled, scheduled: Now.AddHours(5)), [], Now);

        result!.Describe(Now).Should().Be("Rollout scheduled, starts in 5 hours");
    }
}
