using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The mute that keeps a firmware rollout from reading as an outage, and the two evaluators that
/// consult it.
///
/// Two properties matter more than anything else here. The keys have to agree with the readers -
/// a writer colonizing MACs and a reader stripping them would silently disable the whole thing -
/// and the window has to LAPSE when nothing refreshes it, because an orchestrator that died must
/// not leave a site permanently unable to report devices offline.
/// </summary>
public class RolloutSuppressionRegistryTests
{
    private const string Site = "branch-office";
    private const string Mac = "aa:bb:cc:dd:ee:01";
    private static readonly DateTime Now = new(2026, 8, 14, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ARefreshedWindowSuppresses()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);

        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeTrue();
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:01")]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aabbccddee01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    public void EverySpellingOfTheSameMacIsTheSameKey(string spelling)
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);

        registry.IsInRolloutWindow(Site, spelling, Now).Should().BeTrue();
    }

    [Fact]
    public void TheDefaultSiteIsTheSameWhetherItIsNamedOrEmpty()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh("", Mac, Now);

        registry.IsInRolloutWindow(SiteManagementService.DefaultSiteSlug, Mac, Now).Should().BeTrue();
    }

    [Fact]
    public void OneSiteWindowNeverMutesAnother()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);

        registry.IsInRolloutWindow("other-site", Mac, Now).Should().BeFalse();
    }

    [Fact]
    public void AWindowNobodyRefreshesLapses()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);

        registry.IsInRolloutWindow(Site, Mac, Now + RolloutSuppressionRegistry.WindowFreshness).Should().BeTrue();
        registry.IsInRolloutWindow(Site, Mac, Now + RolloutSuppressionRegistry.WindowFreshness + TimeSpan.FromSeconds(1))
            .Should().BeFalse();
    }

    [Fact]
    public void ClearEndsAWindowAtOnce()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);
        registry.Clear(Site, Mac);

        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeFalse();
    }

    [Fact]
    public void ClearingASiteDropsEveryWindowItHeld()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);
        registry.Refresh(Site, "aa:bb:cc:dd:ee:02", Now);
        registry.ClearSite(Site);

        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeFalse();
        registry.IsInRolloutWindow(Site, "aa:bb:cc:dd:ee:02", Now).Should().BeFalse();
    }

    [Fact]
    public void NothingIsSuppressedByDefault()
    {
        var registry = new RolloutSuppressionRegistry();

        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeFalse();
    }

    // --- Console cycling (Network app or UniFi OS restart) ------------------------------------

    [Fact]
    public void ConsoleCycleSuppressesEveryDeviceOnTheSite()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshConsoleCycle(Site, Now);

        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeTrue();
        registry.IsInRolloutWindow(Site, "ff:ff:ff:ff:ff:ff", Now).Should().BeTrue();
        registry.IsInRolloutWindow(Site, null, Now).Should().BeTrue();
    }

    [Fact]
    public void ConsoleCycleImpliesSiteActiveRollout()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshConsoleCycle(Site, Now);

        registry.IsSiteActiveRollout(Site, Now).Should().BeTrue();
    }

    [Fact]
    public void ConsoleCycleLapses()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshConsoleCycle(Site, Now);

        var expired = Now + RolloutSuppressionRegistry.WindowFreshness + TimeSpan.FromSeconds(1);
        registry.IsInRolloutWindow(Site, Mac, expired).Should().BeFalse();
        registry.IsSiteActiveRollout(Site, expired).Should().BeFalse();
    }

    [Fact]
    public void ClearSiteDropsConsoleCycle()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshConsoleCycle(Site, Now);
        registry.ClearSite(Site);

        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeFalse();
        registry.IsSiteActiveRollout(Site, Now).Should().BeFalse();
    }

    // --- OS cycling (UniFi OS update specifically) --------------------------------------------

    [Fact]
    public void OsCycleIsTrackedSeparately()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshOsCycle(Site, Now);

        registry.IsOsCycling(Site, Now).Should().BeTrue();
    }

    [Fact]
    public void ConsoleCycleAloneDoesNotImplyOsCycling()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshConsoleCycle(Site, Now);

        registry.IsOsCycling(Site, Now).Should().BeFalse();
    }

    [Fact]
    public void OsCycleLapses()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshOsCycle(Site, Now);

        var expired = Now + RolloutSuppressionRegistry.WindowFreshness + TimeSpan.FromSeconds(1);
        registry.IsOsCycling(Site, expired).Should().BeFalse();
    }

    [Fact]
    public void ClearSiteDropsOsCycle()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshOsCycle(Site, Now);
        registry.ClearSite(Site);

        registry.IsOsCycling(Site, Now).Should().BeFalse();
    }

    // --- Site-active rollout (device steps in flight) -----------------------------------------

    [Fact]
    public void SiteActiveRolloutFromDeviceSteps()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshSiteActive(Site, Now);

        registry.IsSiteActiveRollout(Site, Now).Should().BeTrue();
    }

    [Fact]
    public void SiteActiveRolloutLapses()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshSiteActive(Site, Now);

        var expired = Now + RolloutSuppressionRegistry.WindowFreshness + TimeSpan.FromSeconds(1);
        registry.IsSiteActiveRollout(Site, expired).Should().BeFalse();
    }

    // --- AP Agent hold (keeps the agent's supervisor off a device mid-upgrade) -----------------

    [Fact]
    public void AgentHoldIsSeparateFromTheAlertWindows()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshAgentHold(Site, Mac, Now);

        registry.IsAgentHeld(Site, Mac, Now).Should().BeTrue();
        registry.IsInRolloutWindow(Site, Mac, Now).Should().BeFalse();
    }

    [Fact]
    public void AlertWindowAloneDoesNotHoldTheAgent()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.Refresh(Site, Mac, Now);

        registry.IsAgentHeld(Site, Mac, Now).Should().BeFalse();
    }

    [Fact]
    public void AgentHoldNobodyRefreshesLapses()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshAgentHold(Site, Mac, Now);

        registry.IsAgentHeld(Site, Mac, Now + RolloutSuppressionRegistry.WindowFreshness).Should().BeTrue();
        registry.IsAgentHeld(Site, Mac, Now + RolloutSuppressionRegistry.WindowFreshness + TimeSpan.FromSeconds(1))
            .Should().BeFalse();
    }

    [Fact]
    public void ClearReleasesTheAgentHold()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshAgentHold(Site, Mac, Now);
        registry.Clear(Site, Mac);

        registry.IsAgentHeld(Site, Mac, Now).Should().BeFalse();
    }

    [Fact]
    public void ClearingASiteDropsItsAgentHolds()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshAgentHold(Site, Mac, Now);
        registry.ClearSite(Site);

        registry.IsAgentHeld(Site, Mac, Now).Should().BeFalse();
    }

    [Fact]
    public void EverySpellingOfTheSameMacIsTheSameAgentHold()
    {
        var registry = new RolloutSuppressionRegistry();
        registry.RefreshAgentHold(Site, Mac, Now);

        registry.IsAgentHeld(Site, "AA-BB-CC-DD-EE-01", Now).Should().BeTrue();
    }

    // --- Evaluator integration ----------------------------------------------------------------

    [Fact]
    public async Task DeviceOfflineIsNotAnnouncedForADeviceTheRolloutIsUpgrading()
    {
        var bus = new CapturingBus();
        var registry = new RolloutSuppressionRegistry();
        var evaluator = new DeviceStateAlertEvaluator(
            bus, new DeviceTransitionTracker(), new DeviceOfflineDeduplicator(), NullLogger<DeviceStateAlertEvaluator>.Instance, Site, registry);
        registry.Refresh(Site, Mac, Now);

        await evaluator.EvaluateAsync(Mac, "AP 1", "192.0.2.10", DeviceType.AccessPoint, 0, Now);
        await evaluator.EvaluateAsync(Mac, "AP 1", "192.0.2.10", DeviceType.AccessPoint, 0, Now.AddSeconds(30));

        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task DeviceOfflineIsAnnouncedAgainOnceTheWindowLapses()
    {
        var bus = new CapturingBus();
        var registry = new RolloutSuppressionRegistry();
        var evaluator = new DeviceStateAlertEvaluator(
            bus, new DeviceTransitionTracker(), new DeviceOfflineDeduplicator(), NullLogger<DeviceStateAlertEvaluator>.Instance, Site, registry);
        registry.Refresh(Site, Mac, Now);

        var late = Now + RolloutSuppressionRegistry.WindowFreshness + TimeSpan.FromMinutes(1);
        await evaluator.EvaluateAsync(Mac, "AP 1", "192.0.2.10", DeviceType.AccessPoint, 0, late);
        await evaluator.EvaluateAsync(Mac, "AP 1", "192.0.2.10", DeviceType.AccessPoint, 0, late.AddSeconds(30));

        bus.Published.Should().ContainSingle().Which.EventType.Should().Be(DeviceStateAlertEvaluator.OfflineEventType);
    }

    [Fact]
    public async Task DeviceRestartedIsNotAnnouncedForADeviceTheRolloutIsUpgrading()
    {
        var bus = new CapturingBus();
        var registry = new RolloutSuppressionRegistry();
        var evaluator = new DeviceRebootAlertEvaluator(
            bus, NullLogger<DeviceRebootAlertEvaluator>.Instance, Site, registry);
        registry.Refresh(Site, Mac, Now);

        var published = await evaluator.EvaluateAsync(
            Mac, "AP 1", "192.0.2.10",
            new DeviceRebootReason(RebootCategory.PowerLoss, "Power loss", "evidence", RebootReasonSource.PstoreConsole),
            bootedAt: Now.AddMinutes(-1), now: Now);

        published.Should().BeFalse();
        bus.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task DeviceRestartedIsAnnouncedForADeviceNoRolloutIsTouching()
    {
        var bus = new CapturingBus();
        var registry = new RolloutSuppressionRegistry();
        var evaluator = new DeviceRebootAlertEvaluator(
            bus, NullLogger<DeviceRebootAlertEvaluator>.Instance, Site, registry);

        var published = await evaluator.EvaluateAsync(
            Mac, "AP 1", "192.0.2.10",
            new DeviceRebootReason(RebootCategory.PowerLoss, "Power loss", "evidence", RebootReasonSource.PstoreConsole),
            bootedAt: Now.AddMinutes(-1), now: Now);

        published.Should().BeTrue();
        bus.Published.Should().ContainSingle();
    }
}
