using System.Runtime.CompilerServices;
using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Zeroes the production waits that exist for real hardware, for every test in this assembly.
/// Each one cost seconds of real time per test and ran classes one test at a time for minutes.
/// </summary>
internal static class TestTimingSetup
{
    [ModuleInitializer]
    internal static void SkipHardwareWaits()
    {
        // The fake console never restages, so each staged channel waited the full 12 s.
        RolloutPlanComposer.CatalogReflectWaitOverride = TimeSpan.Zero;
        // No Wi-Fi link to settle; every recorded result waited 2 s before enrichment.
        ClientSpeedTestService.EnrichmentSettleDelayOverride = TimeSpan.Zero;
    }
}

/// <summary>Pins the production values the overrides above replace, so a test run cannot hide a change to them.</summary>
public class ProductionWaitPinTests
{
    [Fact]
    public void CatalogRestageWaitIsTwelveSecondsPolledEveryTwo()
    {
        RolloutPlanComposer.CatalogReflectWait.Should().Be(TimeSpan.FromSeconds(12));
        RolloutPlanComposer.CatalogPollInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void EnrichmentSettleDelayIsTwoSeconds() =>
        ClientSpeedTestService.EnrichmentSettleDelay.Should().Be(TimeSpan.FromSeconds(2));
}
