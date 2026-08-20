using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// Spacing presets and their advanced-JSON overrides. Malformed JSON must never take the
/// rollout with it - the preset stands - and the floors keep a hand-edited zero from asking
/// the executor for a wave of no devices.
/// </summary>
public class ResolvedSpacingTests
{
    [Fact]
    public void For_Conservative_UsesTheOneAtATimePreset()
    {
        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Conservative, null);

        spacing.ApGapSeconds.Should().Be(180);
        spacing.SwitchGapSeconds.Should().Be(300);
        spacing.GatewayGapSeconds.Should().Be(600);
        spacing.MaxApParallelism.Should().Be(1);
        spacing.MaxSwitchParallelism.Should().Be(1);
    }

    [Fact]
    public void For_Balanced_UsesTheDefaultPreset()
    {
        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Balanced, null);

        spacing.ApGapSeconds.Should().Be(120);
        spacing.SwitchGapSeconds.Should().Be(180);
        spacing.GatewayGapSeconds.Should().Be(300);
        spacing.MaxApParallelism.Should().Be(3);
        spacing.MaxSwitchParallelism.Should().Be(2);
    }

    [Fact]
    public void For_Fast_UsesTheWidestPreset()
    {
        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Fast, null);

        spacing.ApGapSeconds.Should().Be(60);
        spacing.SwitchGapSeconds.Should().Be(90);
        spacing.GatewayGapSeconds.Should().Be(120);
        spacing.MaxApParallelism.Should().Be(6);
        spacing.MaxSwitchParallelism.Should().Be(4);
    }

    [Fact]
    public void For_PartialOverride_LeavesTheRestOfThePresetAlone()
    {
        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Balanced, """{"apGapSeconds":45}""");

        spacing.ApGapSeconds.Should().Be(45);
        spacing.SwitchGapSeconds.Should().Be(180);
        spacing.GatewayGapSeconds.Should().Be(300);
        spacing.MaxApParallelism.Should().Be(3);
        spacing.MaxSwitchParallelism.Should().Be(2);
    }

    [Fact]
    public void For_FullOverride_ReplacesEveryValue()
    {
        var json = """
        {"apGapSeconds":10,"switchGapSeconds":20,"gatewayGapSeconds":30,"maxApParallelism":8,"maxSwitchParallelism":5}
        """;

        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Conservative, json);

        spacing.ApGapSeconds.Should().Be(10);
        spacing.SwitchGapSeconds.Should().Be(20);
        spacing.GatewayGapSeconds.Should().Be(30);
        spacing.MaxApParallelism.Should().Be(8);
        spacing.MaxSwitchParallelism.Should().Be(5);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"apGapSeconds\":\"soon\"}")]
    public void For_InvalidJson_FallsBackToThePreset(string json)
    {
        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Fast, json);

        spacing.ApGapSeconds.Should().Be(60);
        spacing.MaxApParallelism.Should().Be(6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("null")]
    public void For_EmptyOverride_UsesThePreset(string? json)
    {
        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Balanced, json);

        spacing.ApGapSeconds.Should().Be(120);
        spacing.MaxApParallelism.Should().Be(3);
    }

    [Fact]
    public void For_NegativeGaps_AreFlooredAtZero()
    {
        var json = """{"apGapSeconds":-30,"switchGapSeconds":-1,"gatewayGapSeconds":-999}""";

        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Balanced, json);

        spacing.ApGapSeconds.Should().Be(0);
        spacing.SwitchGapSeconds.Should().Be(0);
        spacing.GatewayGapSeconds.Should().Be(0);
    }

    [Fact]
    public void For_ParallelismBelowOne_IsFlooredAtOne()
    {
        var json = """{"maxApParallelism":0,"maxSwitchParallelism":-4}""";

        var spacing = ResolvedSpacing.For(FirmwareSpacingProfile.Fast, json);

        spacing.MaxApParallelism.Should().Be(1);
        spacing.MaxSwitchParallelism.Should().Be(1);
    }
}

/// <summary>
/// Exclusion parsing and matching across the three ways a device can be taken out of a
/// rollout. MACs arrive in whatever shape the console or the user typed, so they normalize
/// on the way in.
/// </summary>
public class RolloutExclusionsTests
{
    private static PlannerDevice Device(string mac, DeviceType type, string model) => new()
    {
        Mac = mac,
        Name = "Device-1",
        Model = model,
        DisplayModel = model,
        Type = type,
    };

    [Fact]
    public void Parse_EmptyOrNull_ExcludesNothing()
    {
        RolloutExclusions.Parse(null).Excludes(Device("aa:bb:cc:dd:ee:01", DeviceType.AccessPoint, "SKU-AP1"))
            .Should().BeFalse();
        RolloutExclusions.Parse("{}").Excludes(Device("aa:bb:cc:dd:ee:01", DeviceType.AccessPoint, "SKU-AP1"))
            .Should().BeFalse();
    }

    [Fact]
    public void Parse_InvalidJson_ExcludesNothing()
    {
        var exclusions = RolloutExclusions.Parse("{not json");

        exclusions.Macs.Should().BeEmpty();
        exclusions.Skus.Should().BeEmpty();
        exclusions.DeviceTypes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:01")]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("AA-BB-CC-DD-EE-01")]
    [InlineData("aabbccddee01")]
    public void Excludes_ByMac_AcceptsAnyInputFormat(string mac)
    {
        var exclusions = RolloutExclusions.Parse($$"""{"macs":["{{mac}}"]}""");

        exclusions.Excludes(Device("aa:bb:cc:dd:ee:01", DeviceType.AccessPoint, "SKU-AP1")).Should().BeTrue();
        exclusions.Excludes(Device("aa:bb:cc:dd:ee:02", DeviceType.AccessPoint, "SKU-AP1")).Should().BeFalse();
    }

    [Fact]
    public void Excludes_BySku_IsCaseInsensitive()
    {
        var exclusions = RolloutExclusions.Parse("""{"skus":["sku-ap1"]}""");

        exclusions.Excludes(Device("aa:bb:cc:dd:ee:01", DeviceType.AccessPoint, "SKU-AP1")).Should().BeTrue();
        exclusions.Excludes(Device("aa:bb:cc:dd:ee:02", DeviceType.AccessPoint, "SKU-AP2")).Should().BeFalse();
    }

    [Theory]
    [InlineData("uap", DeviceType.AccessPoint)]
    [InlineData("usw", DeviceType.Switch)]
    [InlineData("ugw", DeviceType.Gateway)]
    public void Excludes_ByUniFiTypeCode(string code, DeviceType type)
    {
        var exclusions = RolloutExclusions.Parse($$"""{"deviceTypes":["{{code}}"]}""");

        exclusions.Excludes(Device("aa:bb:cc:dd:ee:01", type, "SKU-1")).Should().BeTrue();
    }

    [Theory]
    [InlineData("AccessPoint", DeviceType.AccessPoint)]
    [InlineData("Switch", DeviceType.Switch)]
    [InlineData("Gateway", DeviceType.Gateway)]
    public void Excludes_ByEnumName(string name, DeviceType type)
    {
        var exclusions = RolloutExclusions.Parse($$"""{"deviceTypes":["{{name}}"]}""");

        exclusions.Excludes(Device("aa:bb:cc:dd:ee:01", type, "SKU-1")).Should().BeTrue();
    }

    [Fact]
    public void Excludes_TypeExclusion_LeavesOtherTypesAlone()
    {
        var exclusions = RolloutExclusions.Parse("""{"deviceTypes":["uap"]}""");

        exclusions.Excludes(Device("aa:bb:cc:dd:ee:01", DeviceType.Switch, "SKU-SW1")).Should().BeFalse();
    }
}

/// <summary>
/// The planner keys everything - depth, mesh links, exclusions, neighbor pairs - on one MAC
/// spelling, and reboot events arrive colon-less while device health arrives colonized, so
/// normalization is load-bearing rather than cosmetic.
/// </summary>
public class MacNormalizerTests
{
    [Theory]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aabbccddeeff")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    public void Normalize_AnySupportedSpelling_BecomesLowercaseColons(string mac)
    {
        MacNormalizer.Normalize(mac).Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void Normalize_EmptyOrNull_ReturnsEmpty()
    {
        MacNormalizer.Normalize("").Should().BeEmpty();
        MacNormalizer.Normalize(null!).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GARBAGE", "garbage")]
    [InlineData("  Not A Mac  ", "not a mac")]
    [InlineData("aabbccddee", "aabbccddee")]
    [InlineData("aabbccddeeff00", "aabbccddeeff00")]
    public void Normalize_UnrecognizableInput_PassesThroughLowercased(string input, string expected)
    {
        MacNormalizer.Normalize(input).Should().Be(expected);
    }
}

/// <summary>Type-code mapping used on persisted steps and in exclusion matching.</summary>
public class FirmwareDeviceTypesTests
{
    [Theory]
    [InlineData(DeviceType.AccessPoint, "uap")]
    [InlineData(DeviceType.Switch, "usw")]
    [InlineData(DeviceType.Gateway, "ugw")]
    [InlineData(DeviceType.Unknown, "unknown")]
    public void Code_MapsToTheUniFiShortCode(DeviceType type, string expected)
    {
        FirmwareDeviceTypes.Code(type).Should().Be(expected);
    }
}
