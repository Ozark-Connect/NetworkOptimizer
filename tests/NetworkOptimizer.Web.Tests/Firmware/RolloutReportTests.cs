using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;
using static NetworkOptimizer.Web.Tests.Firmware.RolloutFixtures;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The post-soak report: when it is built, what it says, and that everything it says came from
/// what the rollout already recorded rather than from a fresh measurement.
/// </summary>
public class RolloutReportTests
{
    private static string Stats(double cpu, double memory) =>
        JsonSerializer.Serialize(new RolloutResourceStats { CpuPercent = cpu, MemoryUsedPercent = memory, SampleCount = 12 });

    private static FirmwareRolloutStep Settled(
        string mac,
        FirmwareRolloutStepState state,
        string name = "AP 1",
        string model = "U6PRO",
        DateTime? backAt = null,
        string? pre = null,
        string? post = null,
        string? error = null,
        string? to = ToVersion)
    {
        var step = Step(mac, name, model, state: state, to: to);
        step.BackAt = backAt;
        step.DowntimeSeconds = backAt.HasValue ? 240 : null;
        step.PreStatsJson = pre;
        step.PostStatsJson = post;
        step.Error = error;
        return step;
    }

    [Fact]
    public async Task Report_IsBuiltWhenTheSoakWindowHasPassed()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 24);
        var start = harness.Time.GetUtcNow().UtcDateTime;
        var plan = await harness.SeedSoakingPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            start.AddHours(-1),
            start,
            Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, backAt: start, pre: Stats(10, 40), post: Stats(11, 41)));

        await harness.TickAsync(TimeSpan.FromHours(23));
        (await harness.PlanAsync(plan.Id))!.Status.Should().Be(FirmwareRolloutStatus.SoakWait);
        (await harness.PlanAsync(plan.Id))!.ReportJson.Should().BeNull();

        await harness.TickAsync(TimeSpan.FromHours(2));

        var reported = await harness.PlanAsync(plan.Id);
        reported!.Status.Should().Be(FirmwareRolloutStatus.Reported);
        RolloutReport.Parse(reported.ReportJson).Should().NotBeNull();

        var alert = harness.Bus.Published.Should().ContainSingle().Subject;
        alert.EventType.Should().Be(RolloutAlerts.ReportReady);
        alert.Severity.Should().Be(AlertSeverity.Info);
        alert.SourceUrl.Should().Be(RolloutAlerts.SourceUrl);
    }

    [Fact]
    public async Task Report_CountsEveryOutcomeAndAveragesThePairedWindows()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 1);
        var start = harness.Time.GetUtcNow().UtcDateTime;
        var plan = await harness.SeedSoakingPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            start.AddMinutes(-30),
            start,
            Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, "AP 1",
                backAt: start, pre: Stats(10, 40), post: Stats(20, 50)),
            Settled(PeerMac, FirmwareRolloutStepState.RegressionFlagged, "AP 2",
                backAt: start, pre: Stats(20, 60), post: Stats(40, 70)),
            Settled(SwitchMac, FirmwareRolloutStepState.Failed, "Switch 1", "USW24",
                error: "The device has been offline for over 15 minutes and has not come back."),
            Settled(GatewayMac, FirmwareRolloutStepState.SkippedExcluded, "Gateway", "UCGMAX"));

        await harness.TickAsync(TimeSpan.FromHours(2));

        var report = RolloutReport.Parse((await harness.PlanAsync(plan.Id))!.ReportJson)!;
        report.Rows.Should().HaveCount(4);
        report.DevicesUpgraded.Should().Be(2);
        report.DevicesFailed.Should().Be(1);
        report.DevicesSkipped.Should().Be(1);
        report.DevicesRolledBack.Should().Be(0);
        report.TotalSeconds.Should().Be(1800);

        // Only the two devices measured on both sides feed the site means.
        report.SiteCpuBeforeMean.Should().Be(15);
        report.SiteCpuAfterMean.Should().Be(30);
        report.SiteMemBeforeMean.Should().Be(50);
        report.SiteMemAfterMean.Should().Be(60);

        report.Rows.Single(r => r.Mac == ApMac).Outcome.Should().Be(RolloutOutcomes.Upgraded);
        report.Rows.Single(r => r.Mac == PeerMac).Outcome.Should().Be(RolloutOutcomes.RegressionFlagged);
        report.Rows.Single(r => r.Mac == SwitchMac).Outcome.Should().Be(RolloutOutcomes.Failed);
        report.Rows.Single(r => r.Mac == GatewayMac).Outcome.Should().Be(RolloutOutcomes.Skipped);

        report.Issues.Should().HaveCount(2);
        report.Issues.Should().Contain(i => i.Contains("Switch 1") && i.Contains("offline"));
        report.Issues.Should().Contain(i => i.Contains("AP 2") && i.Contains("working harder"));
    }

    [Fact]
    public async Task Report_CarriesTheConsoleUpdateOutcomesAndThePlansNotes()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 1);
        var start = harness.Time.GetUtcNow().UtcDateTime;

        var document = Document(Wave(1, PlanStep(ApMac)));
        document.IncludesUniFiNetworkUpdate = true;
        document.NetworkAppUpdate.Outcome = "updated";
        document.IncludesUniFiOsUpdate = true;
        document.UniFiOsUpdate.Outcome = "stuck";
        document.UniFiOsUpdate.TargetVersion = "v5.1.28+abc";
        document.Notes.Add("2 U6PRO devices are waiting for 7.0.11 to age 7 days.");

        var plan = await harness.SeedSoakingPlanAsync(document, start.AddMinutes(-10), start,
            Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, backAt: start));

        await harness.TickAsync(TimeSpan.FromHours(2));

        var report = RolloutReport.Parse((await harness.PlanAsync(plan.Id))!.ReportJson)!;
        report.UniFiNetworkUpdateOutcome.Should().Be("updated");
        report.UniFiOsUpdateOutcome.Should().Be("stuck");
        report.Issues.Should().Contain(i => i.Contains("UniFi OS update"));
        report.Notes.Should().ContainSingle().Which.Should().Contain("age 7 days");
    }

    [Fact]
    public async Task Report_CallsADeviceBackOnItsOldFirmwareRolledBack()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 1);
        var start = harness.Time.GetUtcNow().UtcDateTime;

        var document = Document(Wave(1, PlanStep(ApMac)));
        document.PriorVersions.Add(new PlanPriorVersion { Mac = ApMac, Version = FromVersion, Url = "https://example.com/fw.bin" });

        // What a completed rollback leaves behind: the step's target is the version it started on.
        var rolledBack = Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, backAt: start, to: FromVersion);
        rolledBack.FromVersion = ToVersion;

        var plan = await harness.SeedSoakingPlanAsync(document, start.AddMinutes(-10), start, rolledBack);

        await harness.TickAsync(TimeSpan.FromHours(2));

        var report = RolloutReport.Parse((await harness.PlanAsync(plan.Id))!.ReportJson)!;
        report.Rows.Single().Outcome.Should().Be(RolloutOutcomes.RolledBack);
        report.DevicesRolledBack.Should().Be(1);
        report.DevicesUpgraded.Should().Be(0);
    }

    [Fact]
    public async Task Report_LinksTheChangelogWhereTheFeedHasOne()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 1);
        harness.Releases.Set("U6PRO", ToVersion, harness.Time.GetUtcNow().UtcDateTime.AddDays(-10),
            "https://fw-update.ui.com/api/changelog/1");
        var start = harness.Time.GetUtcNow().UtcDateTime;
        var plan = await harness.SeedSoakingPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            start.AddMinutes(-10),
            start,
            Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, backAt: start),
            Settled(SwitchMac, FirmwareRolloutStepState.LitmusPassed, "Switch 1", "USW24", backAt: start));

        await harness.TickAsync(TimeSpan.FromHours(2));

        var report = RolloutReport.Parse((await harness.PlanAsync(plan.Id))!.ReportJson)!;
        report.Rows.Single(r => r.Mac == ApMac).ChangelogUrl.Should().Be("https://fw-update.ui.com/api/changelog/1");
        report.Rows.Single(r => r.Mac == SwitchMac).ChangelogUrl.Should().BeNull();
    }

    [Fact]
    public async Task Report_SurvivesAFeedThatWillNotAnswer()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 1);
        harness.Releases.Throws = true;
        var start = harness.Time.GetUtcNow().UtcDateTime;
        var plan = await harness.SeedSoakingPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            start.AddMinutes(-10),
            start,
            Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, backAt: start));

        await harness.TickAsync(TimeSpan.FromHours(2));

        var report = RolloutReport.Parse((await harness.PlanAsync(plan.Id))!.ReportJson)!;
        report.Rows.Single().ChangelogUrl.Should().BeNull();
        report.DevicesUpgraded.Should().Be(1);
    }

    [Fact]
    public async Task GetReportAsync_HandsThePageTheStoredReportItCanParse()
    {
        using var harness = new RolloutHarness();
        await harness.WithSettingsAsync(s => s.SoakHours = 1);
        var start = harness.Time.GetUtcNow().UtcDateTime;
        var plan = await harness.SeedSoakingPlanAsync(
            Document(Wave(1, PlanStep(ApMac))),
            start.AddMinutes(-10),
            start,
            Settled(ApMac, FirmwareRolloutStepState.LitmusPassed, backAt: start, pre: Stats(10, 40), post: Stats(12, 42)));

        await harness.TickAsync(TimeSpan.FromHours(2));

        var view = await harness.Service.GetReportAsync(plan.Id);
        view!.IsReady.Should().BeTrue();

        var parsed = RolloutReport.Parse(view.ReportJson)!;
        parsed.Rows.Should().ContainSingle().Which.Name.Should().Be("AP 1");
        parsed.Rows.Single().CpuBeforeMean.Should().Be(10);
        parsed.Rows.Single().CpuAfterMean.Should().Be(12);
        parsed.GeneratedAt.Should().BeAfter(start);
    }

    [Fact]
    public void Parse_ReturnsNullForNothingAndForNonsense()
    {
        RolloutReport.Parse(null).Should().BeNull();
        RolloutReport.Parse("").Should().BeNull();
        RolloutReport.Parse("not json").Should().BeNull();
    }
}
