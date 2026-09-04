using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The estimator is the timeline's only source of truth for how long a device is down, so both
/// halves are pinned here: the seed table measured from real upgrade cycles, and the classifier
/// that decides which seed a device gets. Classification matters twice over - it also picks the
/// offline budget, and an unknown gateway landing in the wrong class is the difference between
/// declaring a healthy console stuck at 15 minutes and waiting the 30 it actually needs.
/// </summary>
public class FirmwareTimingEstimatorTests
{
    private static PlannerDevice Device(DeviceType type, string model, string displayModel) => new()
    {
        Mac = "aa:bb:cc:dd:ee:01",
        Name = "Device-1",
        Model = model,
        DisplayModel = displayModel,
        Type = type,
    };

    [Theory]
    [InlineData(FirmwareDeviceClass.AccessPoint, 180)]
    [InlineData(FirmwareDeviceClass.OlderAccessPoint, 240)]
    [InlineData(FirmwareDeviceClass.Switch, 480)]
    [InlineData(FirmwareDeviceClass.GatewayNetworkOnly, 240)]
    [InlineData(FirmwareDeviceClass.CloudGatewayUniFiOs, 360)]
    public void SeedDowntimeSeconds_MatchesMeasuredResearchValues(FirmwareDeviceClass cls, int expected)
    {
        FirmwareTimingEstimator.SeedDowntimeSeconds(cls).Should().Be(expected);
    }

    [Theory]
    [InlineData(FirmwareDeviceClass.AccessPoint, 900)]
    [InlineData(FirmwareDeviceClass.OlderAccessPoint, 900)]
    [InlineData(FirmwareDeviceClass.Switch, 900)]
    [InlineData(FirmwareDeviceClass.GatewayNetworkOnly, 900)]
    [InlineData(FirmwareDeviceClass.CloudGatewayUniFiOs, 1800)]
    public void OfflineBudgetSeconds_OnlyCloudGatewaysGetTheLongBudget(FirmwareDeviceClass cls, int expected)
    {
        FirmwareTimingEstimator.OfflineBudgetSeconds(cls).Should().Be(expected);
    }

    [Fact]
    public void EstimateDowntimeSeconds_NoLearnedTimings_UsesSeed()
    {
        var estimator = new FirmwareTimingEstimator();

        estimator.EstimateDowntimeSeconds("SKU-AP1", FirmwareDeviceClass.AccessPoint).Should().Be(180);
    }

    [Fact]
    public void EstimateDowntimeSeconds_LearnedMedianWithEnoughSamples_BeatsSeed()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "SKU-AP1", SampleCount = 3, MedianDowntimeSeconds = 333 }
        ]);

        estimator.EstimateDowntimeSeconds("SKU-AP1", FirmwareDeviceClass.AccessPoint).Should().Be(333);
    }

    [Fact]
    public void EstimateDowntimeSeconds_BelowMinimumSampleCount_KeepsSeed()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "SKU-AP1", SampleCount = 0, MedianDowntimeSeconds = 333 }
        ]);

        estimator.EstimateDowntimeSeconds("SKU-AP1", FirmwareDeviceClass.AccessPoint).Should().Be(180);
    }

    [Fact]
    public void EstimateDowntimeSeconds_LearnedMedianOfZero_KeepsSeed()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "SKU-AP1", SampleCount = 25, MedianDowntimeSeconds = 0 }
        ]);

        estimator.EstimateDowntimeSeconds("SKU-AP1", FirmwareDeviceClass.AccessPoint).Should().Be(180);
    }

    [Fact]
    public void EstimateDowntimeSeconds_LearnedLookup_IgnoresModelCase()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "sku-ap1", SampleCount = 5, MedianDowntimeSeconds = 275 }
        ]);

        estimator.EstimateDowntimeSeconds("SKU-AP1", FirmwareDeviceClass.AccessPoint).Should().Be(275);
    }

    [Fact]
    public void EstimateDowntimeSeconds_OtherModelsLearned_StillUseTheirSeed()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "SKU-AP1", SampleCount = 5, MedianDowntimeSeconds = 275 }
        ]);

        estimator.EstimateDowntimeSeconds("SKU-SW1", FirmwareDeviceClass.Switch).Should().Be(480);
    }

    [Fact]
    public void EstimateDowntimeSeconds_EmptyModel_UsesSeed()
    {
        var estimator = new FirmwareTimingEstimator([
            new FirmwareModelTiming { Model = "", SampleCount = 9, MedianDowntimeSeconds = 111 }
        ]);

        estimator.EstimateDowntimeSeconds("", FirmwareDeviceClass.Switch).Should().Be(480);
    }

    [Fact]
    public void EstimateDowntimeSeconds_DeviceOverload_ClassifiesThenEstimates()
    {
        var estimator = new FirmwareTimingEstimator();

        estimator.EstimateDowntimeSeconds(Device(DeviceType.AccessPoint, "U7PG2", "UAP-AC-Pro"))
            .Should().Be(240);
    }

    [Theory]
    [InlineData("UXGPRO", "UXG-Pro")]
    [InlineData("UXGLITE", "UXG-Lite")]
    [InlineData("SOMEGW", "UXG-Max")]
    [InlineData("UXGA6AA", "UXG-Fiber")]
    [InlineData("UGW3", "USG-3P")]
    [InlineData("UGWHD4", "USG")]
    [InlineData("UGWXG", "USG-XG-8")]
    public void Classify_GatewaysManagedByAConsoleElsewhere_AreNetworkOnly(string model, string displayModel)
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.Gateway, model, displayModel))
            .Should().Be(FirmwareDeviceClass.GatewayNetworkOnly);
    }

    [Theory]
    [InlineData("UDMPRO", "UDM-Pro")]
    [InlineData("UDR", "UniFi Dream Router")]
    [InlineData("UDRULT", "UCG-Ultra")]
    [InlineData("UDMA6A8", "UCG-Fiber")]
    [InlineData("UCGMAX", "UCG-Max")]
    [InlineData("UX", "UX")]
    [InlineData("SKU-GWX", "Some Unlisted Gateway")]
    public void Classify_CloudAndUnknownGateways_DefaultToTheUniFiOsClass(string model, string displayModel)
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.Gateway, model, displayModel))
            .Should().Be(FirmwareDeviceClass.CloudGatewayUniFiOs);
    }

    [Theory]
    [InlineData("U6PRO", "U6 Pro")]
    [InlineData("U6LITE", "U6 Lite")]
    [InlineData("U7PRO", "U7 Pro")]
    [InlineData("U7PROMAX", "U7 Pro Max")]
    public void Classify_ModernAccessPoints_AreTheFastApClass(string model, string displayModel)
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.AccessPoint, model, displayModel))
            .Should().Be(FirmwareDeviceClass.AccessPoint);
    }

    [Theory]
    [InlineData("U7PG2", "UAP-AC-Pro")]
    [InlineData("U7LT", "AC Lite")]
    [InlineData("UHDIW", "nanoHD")]
    [InlineData("UFLHD", "FlexHD")]
    [InlineData("SKU-OLD", "AC LR")]
    public void Classify_OlderGenerationDisplayNames_AreTheSlowApClass(string model, string displayModel)
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.AccessPoint, model, displayModel))
            .Should().Be(FirmwareDeviceClass.OlderAccessPoint);
    }

    [Fact]
    public void Classify_UapModelCode_IsOlderWhenNoProductNameResolved()
    {
        // GetBestProductName hands back the SKU itself when it knows nothing about it, which is
        // the only case the SKU heuristic is allowed to speak for.
        FirmwareTimingEstimator.Classify(Device(DeviceType.AccessPoint, "UAPXYZ", "UAPXYZ"))
            .Should().Be(FirmwareDeviceClass.OlderAccessPoint);
    }

    [Theory]
    [InlineData("UAPA6A6", "U7-Pro-Outdoor")]
    [InlineData("UAPA6AC", "U7-Pro-XGS-B")]
    [InlineData("UAPA697", "E7")]
    public void Classify_ModernApWhoseSkuStartsUap_IsNotOlder(string model, string displayModel)
    {
        // Every current AP SKU is spelled UAPxxxx. Letting the SKU override a resolved product
        // name put U7 and E7 hardware on the older-generation seed.
        FirmwareTimingEstimator.Classify(Device(DeviceType.AccessPoint, model, displayModel))
            .Should().Be(FirmwareDeviceClass.AccessPoint);
    }

    [Fact]
    public void Classify_Uap6ModelCode_IsExemptedFromTheUapMarker()
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.AccessPoint, "UAP6MP", "U6-Pro"))
            .Should().Be(FirmwareDeviceClass.AccessPoint);
    }

    [Theory]
    [InlineData("USL24P", "USW-24-PoE")]
    [InlineData("USL8LP", "USW-Lite-8-PoE")]
    [InlineData("SKU-SW1", "SKU-SW1")]
    public void Classify_Switches_AreTheSwitchClass(string model, string displayModel)
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.Switch, model, displayModel))
            .Should().Be(FirmwareDeviceClass.Switch);
    }

    [Fact]
    public void Classify_UnknownDeviceType_FallsBackToTheSwitchClass()
    {
        FirmwareTimingEstimator.Classify(Device(DeviceType.Unknown, "SKU-X1", "SKU-X1"))
            .Should().Be(FirmwareDeviceClass.Switch);
    }
}
