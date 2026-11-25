using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>An HA gateway pair upgrades one unit per wave, the standby first.</summary>
public class RolloutPlannerHaTests
{
    private const string PrimaryMac = "aa:bb:cc:dd:ee:01";
    private const string ShadowMac = "aa:bb:cc:dd:ee:02";

    private static PlannerDevice Gateway(string mac, bool active) => new()
    {
        Mac = mac,
        Name = "Gateway",
        Model = "UXGENT",
        DisplayModel = "UXG-Enterprise",
        Type = DeviceType.Gateway,
        IsActiveGateway = active,
        Upgradable = true,
        FromVersion = "1.0.0",
        ToVersion = "1.1.0",
    };

    private static RolloutPlanResult Plan(params PlannerDevice[] devices) =>
        new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = devices.ToList(),
            Settings = new FirmwareRolloutSettings { GlobalChannel = FirmwareChannels.Release },
            Estimator = new FirmwareTimingEstimator(),
            CurrentConsoleChannel = FirmwareChannels.Release,
        });

    [Fact]
    public void Pair_StandbyFirst_ActiveLast_NeverTogether()
    {
        var doc = Plan(Gateway(PrimaryMac, active: true), Gateway(ShadowMac, active: false)).Document;

        var gatewayWaves = doc.Waves.Where(w => w.Steps.Any(s => s.Mac is PrimaryMac or ShadowMac)).ToList();
        gatewayWaves.Should().HaveCount(2);
        gatewayWaves.Should().OnlyContain(w => w.Steps.Count == 1);
        gatewayWaves[0].Steps[0].Mac.Should().Be(ShadowMac);
        gatewayWaves[1].Steps[0].Mac.Should().Be(PrimaryMac);
        gatewayWaves[1].MayOverlapWaves.Should().NotContain(gatewayWaves[0].Number);
    }

    [Fact]
    public void AfterFailover_TheBackupPrimaryGoesFirst()
    {
        var doc = Plan(Gateway(PrimaryMac, active: false), Gateway(ShadowMac, active: true)).Document;

        doc.Waves[^2].Steps[0].Mac.Should().Be(PrimaryMac);
        doc.Waves[^1].Steps[0].Mac.Should().Be(ShadowMac);
    }
}
