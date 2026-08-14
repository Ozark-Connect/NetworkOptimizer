using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

/// <summary>
/// The one firmware shortener. Every display surface goes through it, so the shapes the fleet
/// actually reports are pinned here rather than in each caller.
/// </summary>
public class FirmwareVersionFormatTests
{
    [Theory]
    // Console catalog and device fields.
    [InlineData("7.5.10.17129", "7.5.10")]
    [InlineData("6.6.55.1234", "6.6.55")]
    [InlineData("7.5.10", "7.5.10")]
    // Console-reported console firmware, platform prefixed with a hash and build stamp.
    [InlineData("UXGA6AA.ipq9574.v5.1.26.0bc0fe4.260716.1128", "5.1.26")]
    // Switch upgrade marker, underscore separated with a plus-stamped build.
    [InlineData("US3.rtl93xx_7.5.6+17090.260622.0846", "7.5.6")]
    // Release feed spelling.
    [InlineData("v5.1.28+baa7152", "5.1.28")]
    public void Short_KeepsTheVersionAndDropsTheBuildDetail(string input, string expected) =>
        FirmwareVersionFormat.Short(input).Should().Be(expected);

    [Theory]
    [InlineData("4.3", "4.3")]
    [InlineData("v4.3", "4.3")]
    public void Short_KeepsTwoComponentVersionsOlderBuildsStillReport(string input, string expected) =>
        FirmwareVersionFormat.Short(input).Should().Be(expected);

    [Fact]
    public void Short_DoesNotReadAGitHashAsAVersionComponent() =>
        FirmwareVersionFormat.Short("v5.1.b3a286b").Should().Be("5.1");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Short_PassesBlankStringsThrough(string input) =>
        FirmwareVersionFormat.Short(input).Should().Be(input);

    [Fact]
    public void Short_PassesThroughSomethingWithNoVersionInIt() =>
        FirmwareVersionFormat.Short("unknown").Should().Be("unknown");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ShortOrNull_IsNullWhenThereIsNothingToShow(string? input) =>
        FirmwareVersionFormat.ShortOrNull(input).Should().BeNull();

    [Fact]
    public void ShortOrNull_ShortensWhenThereIs() =>
        FirmwareVersionFormat.ShortOrNull("7.5.10.17129").Should().Be("7.5.10");
}
