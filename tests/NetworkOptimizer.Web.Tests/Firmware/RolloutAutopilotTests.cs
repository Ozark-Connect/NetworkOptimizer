using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Autopilot's judgement, which is entirely about when NOT to plan: while anything is in flight,
/// while the last unattended run was stopped and nothing new has shipped since, and - when the site
/// asks for it - while a build is still too fresh to trust unattended.
/// </summary>
public class RolloutAutopilotTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:01";
    private const string PeerMac = "aa:bb:cc:dd:ee:02";
    private const string GatewayMac = "aa:bb:cc:dd:ee:04";

    private static PlannerDevice Ap(
        string mac, string name, string model = "SKU-AP1", string toVersion = "1.1.0") => new()
        {
            Mac = mac,
            Name = name,
            Model = model,
            DisplayModel = model,
            Type = DeviceType.AccessPoint,
            Upgradable = true,
            FromVersion = "1.0.0",
            ToVersion = toVersion,
            IpAddress = "192.0.2.10",
        };

    private static PlannerDevice CloudGateway() => new()
    {
        Mac = GatewayMac,
        Name = "Gateway",
        Model = "UCGMAX",
        DisplayModel = "Cloud Gateway Max",
        Type = DeviceType.Gateway,
        Upgradable = true,
        FromVersion = "4.0.0",
        ToVersion = "4.1.0",
        IpAddress = "192.0.2.1",
    };

    /// <summary>A site on autopilot with two upgradable APs of different models.</summary>
    private static async Task<RolloutHarness> AutopilotSiteAsync(Action<FirmwareRolloutSettings>? configure = null)
    {
        var harness = new RolloutHarness();
        harness.Planning.Devices.Add(Ap(ApMac, "AP 1"));
        harness.Planning.Devices.Add(Ap(PeerMac, "AP 2", model: "SKU-AP2"));
        await harness.WithSettingsAsync(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.IncludeUniFiNetwork = false;
            s.IncludeUniFiOs = false;
            configure?.Invoke(s);
        });
        return harness;
    }

    [Fact]
    public async Task CreatePlanIfDue_AnnouncesAPlanIntoTheProposedWindow()
    {
        using var harness = await AutopilotSiteAsync();

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        planId.Should().NotBeNull();
        var plan = await harness.PlanAsync(planId!.Value);
        plan!.Status.Should().Be(FirmwareRolloutStatus.Announced);
        plan.CreatedBy.Should().Be(RolloutAutopilot.Actor);
        plan.ScheduledStartAt.Should().Be(RolloutAutopilot.ToUtc(harness.Planning.Window.StartLocal));

        var steps = await harness.Repository.GetStepsAsync(plan.Id);
        steps.Should().HaveCount(2);
        steps.Should().OnlyContain(s => s.State != FirmwareRolloutStepState.SkippedExcluded);

        var alert = harness.Bus.Published.Should().ContainSingle().Subject;
        alert.EventType.Should().Be(RolloutAlerts.Upcoming);
        alert.Severity.Should().Be(AlertSeverity.Info);
        alert.SourceUrl.Should().Be(RolloutAlerts.SourceUrl);
        alert.Message.Should().Contain("postpone");
    }

    [Fact]
    public async Task CreatePlanIfDue_AsksForAWindowAtLeastTheHeadsUpHoursAway()
    {
        using var harness = await AutopilotSiteAsync(s => s.NotifyHoursAhead = 18);

        await harness.Autopilot.CreatePlanIfDueAsync();

        harness.Planning.LastMinLead.Should().Be(TimeSpan.FromHours(18));
    }

    [Fact]
    public async Task CreatePlanIfDue_DoesNothingWhileAPlanIsAlreadyInFlight()
    {
        using var harness = await AutopilotSiteAsync();
        await harness.SeedScheduledPlanAsync(
            RolloutFixtures.Document(RolloutFixtures.Wave(1, RolloutFixtures.PlanStep(ApMac))),
            harness.Time.GetUtcNow().UtcDateTime.AddHours(12),
            RolloutFixtures.Step(ApMac));

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        planId.Should().BeNull();
        harness.Bus.Published.Should().BeEmpty();
        (await harness.Repository.GetPlanHistoryAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task CreatePlanIfDue_DoesNothingWhenTheSiteIsNotOnAutopilot()
    {
        using var harness = await AutopilotSiteAsync(s => s.Mode = FirmwareRolloutMode.ManualOnly);

        (await harness.Autopilot.CreatePlanIfDueAsync()).Should().BeNull();
        harness.Planning.ContextCalls.Should().Be(0);
    }

    [Fact]
    public async Task CreatePlanIfDue_LooksAtTheSiteAtMostOnceAnHour()
    {
        using var harness = await AutopilotSiteAsync();

        await harness.Autopilot.CreatePlanIfDueAsync();
        harness.Time.Advance(TimeSpan.FromMinutes(30));
        await harness.Autopilot.CreatePlanIfDueAsync();

        harness.Planning.ContextCalls.Should().Be(1);
    }

    [Fact]
    public async Task CreatePlanIfDue_DoesNotRecreateAnAbortedPlanUntilSomethingNewShips()
    {
        using var harness = await AutopilotSiteAsync();
        var first = await harness.Autopilot.CreatePlanIfDueAsync();
        first.Should().NotBeNull();

        await harness.Orchestrator.AbortAsync("an admin stopped it");
        harness.Time.Advance(TimeSpan.FromHours(2));

        (await harness.Autopilot.CreatePlanIfDueAsync()).Should().BeNull();

        // A newer build for one of the two APs IS new content, so the offer comes back.
        harness.Planning.Devices[0] = Ap(ApMac, "AP 1", toVersion: "1.2.0");
        harness.Time.Advance(TimeSpan.FromHours(2));

        (await harness.Autopilot.CreatePlanIfDueAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task RipenessGate_HoldsBackDevicesWhoseBuildIsStillTooNew()
    {
        using var harness = await AutopilotSiteAsync(s => s.MinReleaseAgeDays = 7);
        var now = harness.Time.GetUtcNow().UtcDateTime;
        harness.Releases.Set("SKU-AP1", "1.1.0", now.AddDays(-2));
        harness.Releases.Set("SKU-AP2", "1.1.0", now.AddDays(-30));

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        var steps = await harness.Repository.GetStepsAsync(planId!.Value);
        steps.Single(s => s.DeviceMac == ApMac).State.Should().Be(FirmwareRolloutStepState.SkippedExcluded);
        steps.Single(s => s.DeviceMac == PeerMac).State.Should().NotBe(FirmwareRolloutStepState.SkippedExcluded);

        var plan = await harness.PlanAsync(planId.Value);
        plan!.PlanJson.Should().Contain("waiting for 1.1.0 to age 7 days");
    }

    [Fact]
    public async Task RipenessGate_TreatsABuildItCannotDateAsRipe()
    {
        using var harness = await AutopilotSiteAsync(s => s.MinReleaseAgeDays = 7);
        harness.Releases.Throws = true;

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        var steps = await harness.Repository.GetStepsAsync(planId!.Value);
        steps.Should().OnlyContain(s => s.State != FirmwareRolloutStepState.SkippedExcluded);
        (await harness.PlanAsync(planId.Value))!.PlanJson.Should().Contain("No publish date could be read");
    }

    [Fact]
    public async Task RipenessGate_PlansNothingWhenEveryBuildIsTooNew()
    {
        using var harness = await AutopilotSiteAsync(s => s.MinReleaseAgeDays = 7);
        var now = harness.Time.GetUtcNow().UtcDateTime;
        harness.Releases.Set("SKU-AP1", "1.1.0", now.AddDays(-1));
        harness.Releases.Set("SKU-AP2", "1.1.0", now.AddDays(-1));

        (await harness.Autopilot.CreatePlanIfDueAsync()).Should().BeNull();
        harness.Bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePlanIfDue_RunsForAConsoleUpdateWithNoDeviceWaiting()
    {
        // A Cloud Gateway reports upgradable=false while its own UniFi OS build waits, so a site
        // can have a real update and not one upgradable device. Counting devices alone left such
        // a site behind for good.
        using var harness = new RolloutHarness();
        harness.Planning.Devices.Add(new PlannerDevice
        {
            Mac = GatewayMac,
            Name = "Gateway",
            Model = "UCGMAX",
            DisplayModel = "Cloud Gateway Max",
            Type = DeviceType.Gateway,
            Upgradable = false,
            FromVersion = "4.0.0",
            IpAddress = "192.0.2.1",
        });
        await harness.WithSettingsAsync(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.IncludeUniFiNetwork = false;
            s.IncludeUniFiOs = true;
        });
        harness.Commands.ConsoleInfo = new UniFiConsoleSystemInfo
        {
            Hardware = new UniFiConsoleHardware { FirmwareVersion = "5.1.28" },
            Firmware = new UniFiConsoleFirmware
            {
                ReleaseChannel = FirmwareChannels.Release,
                LatestByChannel = new Dictionary<string, UniFiConsoleFirmwareRelease>
                {
                    [FirmwareChannels.Release] = new() { Version = "v5.1.30", Channel = FirmwareChannels.Release },
                },
            },
        };

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        planId.Should().NotBeNull("a console update is reason enough to run");
        var plan = await harness.PlanAsync(planId!.Value);
        plan!.PlanJson.Should().Contain("\"IncludesUniFiOsUpdate\":true");
    }

    [Fact]
    public async Task RipenessGate_DropsTheUniFiOsStepWhenTheConsoleBuildIsTooNew()
    {
        using var harness = new RolloutHarness();
        harness.Planning.Devices.Add(CloudGateway());
        await harness.WithSettingsAsync(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.IncludeUniFiNetwork = false;
            s.IncludeUniFiOs = true;
            s.MinReleaseAgeDays = 7;
        });
        harness.Releases.Set("UCGMAX", "4.1.0", harness.Time.GetUtcNow().UtcDateTime.AddDays(-30));
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease
        {
            Version = "v5.1.28+abc",
            Created = harness.Time.GetUtcNow().UtcDateTime.AddDays(-1),
        };

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        var plan = await harness.PlanAsync(planId!.Value);
        plan!.PlanJson.Should().Contain("waiting to age 7 days");
        plan.PlanJson.Should().Contain("\"IncludesUniFiOsUpdate\":false");
    }

    [Fact]
    public async Task RipenessGate_KeepsTheUniFiOsStepOnceTheConsoleBuildHasAged()
    {
        using var harness = new RolloutHarness();
        harness.Planning.Devices.Add(CloudGateway());
        await harness.WithSettingsAsync(s =>
        {
            s.Mode = FirmwareRolloutMode.Autopilot;
            s.IncludeUniFiNetwork = false;
            s.IncludeUniFiOs = true;
            s.MinReleaseAgeDays = 7;
        });
        harness.Releases.Set("UCGMAX", "4.1.0", harness.Time.GetUtcNow().UtcDateTime.AddDays(-30));
        harness.Commands.PendingUniFiOs = new UniFiConsoleFirmwareRelease
        {
            Version = "v5.1.28+abc",
            Created = harness.Time.GetUtcNow().UtcDateTime.AddDays(-30),
        };

        var planId = await harness.Autopilot.CreatePlanIfDueAsync();

        (await harness.PlanAsync(planId!.Value))!.PlanJson.Should().Contain("\"IncludesUniFiOsUpdate\":true");
    }
}
