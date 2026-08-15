using FluentAssertions;
using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Color variants of one product carry their own console model codes, and anything reasoning about
/// a model rather than a device has to see through that. The negative cases matter as much: -M and
/// -X are different products, not colors, and merging them would pair a mesh AP with its wired
/// sibling.
/// </summary>
public class ModelFamilyTests
{
    [Theory]
    [InlineData("UAPA6A9", "U7-Pro-XG")]   // white
    [InlineData("UAPA6AE", "U7-Pro-XG")]   // black, own model code
    [InlineData("UAPA6A4", "U7-Pro-XGS")]
    [InlineData("UAPA6AC", "U7-Pro-XGS")]
    public void GetModelFamily_ColorVariants_ShareOneKey(string modelCode, string expected) =>
        UniFiProductDatabase.GetModelFamily(modelCode).Should().Be(expected);

    [Fact]
    public void GetModelFamily_ALineWithNoBareName_StillPairs()
    {
        // UNAS-2 exists only as -B and -W, so folding one into the other would leave them apart.
        UniFiProductDatabase.GetModelFamily("UNAS2B")
            .Should().Be(UniFiProductDatabase.GetModelFamily("UNAS2W"));
    }

    [Theory]
    [InlineData("UAP-AC-M")]   // Mesh, not a color
    [InlineData("UVP-X")]
    public void GetModelFamily_SuffixesThatAreNotColors_AreLeftAlone(string productName) =>
        UniFiProductDatabase.GetModelFamily(productName).Should().Be(productName);

    [Fact]
    public void GetModelFamily_DifferentProducts_StayApart() =>
        UniFiProductDatabase.GetModelFamily("UAPA6A9")
            .Should().NotBe(UniFiProductDatabase.GetModelFamily("UAPA6A4"));

    [Fact]
    public void GetModelFamily_AModelTheCatalogDoesNotCarry_ComesBackUnchanged() =>
        UniFiProductDatabase.GetModelFamily("NOTAREALCODE").Should().Be("NOTAREALCODE");
}
