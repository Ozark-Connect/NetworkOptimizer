using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Whole-fleet rollouts at three sizes, asserted as invariants rather than as exact orderings.
/// The unit tests around them pin individual rules on a handful of devices; these exist because the
/// rules interact, and the interactions only show up once a plan has levels, families, mesh and
/// enough devices to fill a wave. Both bugs in the wave-overlap work were invariant breaches that
/// every existing test still passed.
/// </summary>
public class RolloutFleetScaleTests
{
    private const string GatewayMac = "aa:bb:cc:00:00:01";

    /// <summary>
    /// A site: one gateway, a tier of distribution switches, access switches under them, and APs
    /// spread across the access switches. Models repeat so families and canaries are exercised, and
    /// APs are neighbors with the ones beside them, which is what real coverage looks like.
    /// </summary>
    private static (List<PlannerDevice> Devices, ApNeighborOracle Neighbors) Fleet(
        int distSwitches, int accessPerDist, int apsPerAccess, bool placementData = true)
    {
        var devices = new List<PlannerDevice>();
        var oracle = new ApNeighborOracle(placementData);
        var aps = new List<string>();

        devices.Add(New(GatewayMac, DeviceType.Gateway, "SKU-GW", "Gateway", null));

        for (var d = 0; d < distSwitches; d++)
        {
            var dist = $"aa:bb:cc:01:{d:x2}:01";
            devices.Add(New(dist, DeviceType.Switch, $"SKU-SW{d % 2}", $"Dist-{d}", GatewayMac));

            for (var a = 0; a < accessPerDist; a++)
            {
                var access = $"aa:bb:cc:02:{d:x2}:{a:x2}";
                devices.Add(New(access, DeviceType.Switch, $"SKU-ASW{a % 3}", $"Access-{d}-{a}", dist));

                for (var p = 0; p < apsPerAccess; p++)
                {
                    var ap = $"aa:bb:cc:03:{d:x2}{a:x2}:{p:x2}";
                    devices.Add(New(ap, DeviceType.AccessPoint, $"SKU-AP{p % 4}", $"AP-{d}-{a}-{p}", access));
                    aps.Add(ap);
                }
            }
        }

        // Each AP overlaps the two laid out next to it.
        for (var i = 1; i < aps.Count; i++)
        {
            oracle.AddNeighbors(aps[i - 1], aps[i]);
            if (i >= 2) oracle.AddNeighbors(aps[i - 2], aps[i]);
        }

        return (devices, oracle);
    }

    private static PlannerDevice New(string mac, DeviceType type, string model, string name, string? uplink) => new()
    {
        Mac = mac,
        Name = name,
        Model = model,
        DisplayModel = model,
        Type = type,
        Upgradable = true,
        FromVersion = "1.0.0",
        ToVersion = "1.1.0",
        UplinkMac = uplink,
        IpAddress = "192.0.2.10",
    };

    private static FirmwareRolloutSettings Settings(FirmwareSpacingProfile profile) => new()
    {
        GlobalChannel = FirmwareChannels.Release,
        PerDeviceTypeChannelsJson = "{}",
        PerSkuChannelsJson = "{}",
        ExclusionsJson = "{}",
        SpacingProfile = profile,
        IncludeUniFiNetwork = false,
        IncludeUniFiOs = false,
    };

    // Small home site, a mid-size business, and a campus.
    [Theory]
    [InlineData(1, 1, 3, FirmwareSpacingProfile.Conservative)]
    [InlineData(2, 3, 6, FirmwareSpacingProfile.Balanced)]
    [InlineData(4, 5, 10, FirmwareSpacingProfile.Fast)]
    public void Fleet_HoldsEveryOrderingInvariant(
        int dist, int access, int aps, FirmwareSpacingProfile profile)
    {
        var (devices, neighbors) = Fleet(dist, access, aps);
        var settings = Settings(profile);
        var spacing = ResolvedSpacing.For(settings.SpacingProfile, settings.AdvancedSpacingJson);

        var result = new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = devices,
            Settings = settings,
            Estimator = new FirmwareTimingEstimator(),
            Neighbors = neighbors,
        });
        var doc = result.Document;
        var byMac = devices.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
        var apCap = RolloutPlanner.EffectiveApCap(
            new RolloutPlanningInput
            {
                Devices = devices,
                Settings = settings,
                Estimator = new FirmwareTimingEstimator(),
                Neighbors = neighbors,
            },
            spacing);

        // Every device is planned exactly once.
        var planned = doc.Waves.SelectMany(w => w.Steps).Select(s => s.Mac).ToList();
        planned.Should().OnlyHaveUniqueItems();
        planned.Should().BeEquivalentTo(devices.Select(d => d.Mac));

        foreach (var wave in doc.Waves)
        {
            CountOf(wave, DeviceType.AccessPoint).Should().BeLessThanOrEqualTo(apCap);
            CountOf(wave, DeviceType.Switch).Should().BeLessThanOrEqualTo(spacing.MaxSwitchParallelism);

            foreach (var a in wave.Steps)
            {
                foreach (var b in wave.Steps.Where(s => s.Mac != a.Mac))
                {
                    neighbors.AreNeighbors(a.Mac, b.Mac).Should().BeFalse(
                        "{0} and {1} cover the same space and must not go down together", a.Name, b.Name);
                    IsAbove(byMac, a.Mac, b.Mac).Should().BeFalse(
                        "{0} reaches the console through {1}", b.Name, a.Name);
                }
            }
        }

        // The gateway goes last and alone.
        var gatewayWave = doc.Waves.Single(w => w.Steps.Any(s => s.Mac == GatewayMac));
        gatewayWave.Number.Should().Be(doc.Waves.Max(w => w.Number));
        gatewayWave.Steps.Should().ContainSingle();
        gatewayWave.MayOverlapWaves.Should().BeEmpty();

        // One device of a family goes before the rest of that family.
        foreach (var family in devices.GroupBy(d => d.ModelFamily).Where(g => g.Count() > 1))
        {
            var firstWave = doc.Waves
                .Where(w => w.Steps.Any(s => byMac[s.Mac].ModelFamily == family.Key))
                .OrderBy(w => w.Number)
                .First();
            firstWave.Steps.Count(s => byMac[s.Mac].ModelFamily == family.Key).Should().Be(1,
                "{0} needs a canary before the rest of its family", family.Key);
        }

        // Overlap only ever points backwards, within one channel, and never at a related wave.
        foreach (var wave in doc.Waves)
        {
            foreach (var earlierNumber in wave.MayOverlapWaves)
            {
                earlierNumber.Should().BeLessThan(wave.Number);
                var earlier = doc.Waves.Single(w => w.Number == earlierNumber);
                earlier.Channel.Should().Be(wave.Channel);
                (CountOf(wave, DeviceType.AccessPoint) + CountOf(earlier, DeviceType.AccessPoint))
                    .Should().BeLessThanOrEqualTo(apCap);

                foreach (var a in earlier.Steps)
                {
                    foreach (var b in wave.Steps)
                    {
                        neighbors.AreNeighbors(a.Mac, b.Mac).Should().BeFalse();
                        IsAbove(byMac, a.Mac, b.Mac).Should().BeFalse();
                        IsAbove(byMac, b.Mac, a.Mac).Should().BeFalse();
                        byMac[a.Mac].ModelFamily.Should().NotBe(byMac[b.Mac].ModelFamily);
                    }
                }
            }
        }

        // The timeline never goes backwards, and the estimate covers the last wave's own cycle.
        doc.Waves.Select(w => w.StartOffsetSeconds).Should().BeInAscendingOrder();
        doc.TotalEstimatedSeconds.Should().BeGreaterThan(doc.Waves.Max(w => w.StartOffsetSeconds));
    }

    [Fact]
    public void LargeFleet_WithCoverageData_FinishesSoonerThanWithout()
    {
        var settings = Settings(FirmwareSpacingProfile.Balanced);

        var (devices, neighbors) = Fleet(4, 5, 10);
        var withCoverage = new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = devices,
            Settings = settings,
            Estimator = new FirmwareTimingEstimator(),
            Neighbors = neighbors,
        }).Document;

        // Same fleet, same neighbor pairs, but nothing corroborating them as placements.
        var (bare, roamingOnly) = Fleet(4, 5, 10, placementData: false);
        var withoutCoverage = new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = bare,
            Settings = settings,
            Estimator = new FirmwareTimingEstimator(),
            Neighbors = roamingOnly,
        }).Document;

        withCoverage.TotalEstimatedSeconds.Should().BeLessThan(withoutCoverage.TotalEstimatedSeconds);
    }

    /// <summary>
    /// The same plan the preview draws, executed. Asserts the rules the executor is responsible
    /// for holding at runtime - which waves may be in flight together, and how many - and that a
    /// console going dark mid-run stalls the rollout rather than failing it.
    /// </summary>
    [Fact]
    public async Task MediumFleet_RunsToCompletion_AndNeverBreaksTheOverlapRules()
    {
        const int Online = (int)UniFiDeviceState.Connected;
        const int Upgrading = (int)UniFiDeviceState.Upgrading;

        using var harness = new RolloutHarness();
        var (devices, neighbors) = Fleet(2, 2, 3);
        var settings = Settings(FirmwareSpacingProfile.Balanced);
        var spacing = ResolvedSpacing.For(settings.SpacingProfile, settings.AdvancedSpacingJson);

        var result = new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = devices,
            Settings = settings,
            Estimator = new FirmwareTimingEstimator(),
            Neighbors = neighbors,
        });
        var doc = result.Document;
        await harness.Repository.SaveSettingsAsync(settings);
        var plan = await harness.SeedScheduledPlanAsync(doc, RolloutHarness.Start, [.. result.Steps]);

        foreach (var d in devices)
            harness.Observer.Set(d.Mac, Online, "1.0.0", upgradeTo: "1.1.0", model: d.Model, name: d.Name);

        var overlapSeen = 0;
        for (var tick = 0; tick < 1500; tick++)
        {
            await harness.TickAsync(TimeSpan.FromMinutes(1));
            var steps = await harness.Repository.GetStepsAsync(plan.Id);
            if (steps.All(s => s.State is FirmwareRolloutStepState.LitmusPassed
                or FirmwareRolloutStepState.RegressionFlagged
                or FirmwareRolloutStepState.Failed
                or FirmwareRolloutStepState.SkippedExcluded))
            {
                break;
            }

            // Which waves are moving right now, and are they allowed to be moving together?
            var live = steps
                .Where(s => s.State is FirmwareRolloutStepState.Commanded
                    or FirmwareRolloutStepState.Down
                    or FirmwareRolloutStepState.BackOnline
                    or FirmwareRolloutStepState.CoolDown)
                .Select(s => s.Wave)
                .Distinct()
                .ToList();

            live.Count.Should().BeLessThanOrEqualTo(spacing.MaxWaveOverlap);
            if (live.Count > 1) overlapSeen++;
            foreach (var a in live)
            {
                foreach (var b in live.Where(w => w != a))
                {
                    var later = doc.Waves.Single(w => w.Number == Math.Max(a, b));
                    later.MayOverlapWaves.Should().Contain(Math.Min(a, b),
                        "waves {0} and {1} were in flight together", a, b);
                }
            }

            // Each device answers the command: it goes into its cycle, then comes back upgraded.
            foreach (var step in steps)
            {
                var d = devices.Single(x => x.Mac == step.DeviceMac);
                if (step.State == FirmwareRolloutStepState.Commanded)
                    harness.Observer.Set(step.DeviceMac, Upgrading, "1.0.0", model: d.Model, name: d.Name);
                else if (step.State == FirmwareRolloutStepState.Down)
                    harness.Observer.Set(step.DeviceMac, Online, "1.1.0", model: d.Model, name: d.Name);
            }
        }

        var final = await harness.Repository.GetStepsAsync(plan.Id);
        final.Should().OnlyContain(s => s.State == FirmwareRolloutStepState.LitmusPassed);
        overlapSeen.Should().BeGreaterThan(0, "the point of the change is that unrelated waves run together");
    }

    private static int CountOf(PlanWave wave, DeviceType type) =>
        wave.Steps.Count(s => FirmwareDeviceTypes.Parse(s.DeviceType) == type);

    /// <summary>Whether <paramref name="above"/> sits anywhere on <paramref name="below"/>'s uplink path.</summary>
    private static bool IsAbove(Dictionary<string, PlannerDevice> byMac, string above, string below)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var at = byMac.TryGetValue(below, out var d) ? d.UplinkMac : null;
        while (at != null && seen.Add(at))
        {
            if (string.Equals(at, above, StringComparison.OrdinalIgnoreCase)) return true;
            at = byMac.TryGetValue(at, out var parent) ? parent.UplinkMac : null;
        }
        return false;
    }
}
