using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

/// <summary>
/// SFP DDM fills its vendor field in upper case, so the raw string shouts where
/// the brand does not. The fix is an allowlist, not a title-case pass.
/// </summary>
public class BrandCasingTests
{
    [Theory]
    [InlineData("CALIX", "Calix")]
    [InlineData("NOKIA", "Nokia")]
    [InlineData("HUAWEI", "Huawei")]
    [InlineData("UBIQUITI", "Ubiquiti")]
    public void CleanOrgName_FixesVendorsThatAreNotBrandedInCaps(string raw, string expected)
    {
        NetworkFormatHelpers.CleanOrgName(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("ZTE")]
    [InlineData("FS")]
    [InlineData("HPE")]
    public void CleanOrgName_LeavesGenuinelyCapitalizedBrandsAlone(string raw)
    {
        NetworkFormatHelpers.CleanOrgName(raw).Should().Be(raw);
    }

    [Fact]
    public void CleanOrgName_IsCaseSensitiveSoAProperlyCasedNameIsUntouched()
    {
        NetworkFormatHelpers.CleanOrgName("Calix").Should().Be("Calix");
    }

    [Fact]
    public void CleanOrgName_StillStripsSuffixesBeforeCasing()
    {
        NetworkFormatHelpers.CleanOrgName("CALIX, Inc.").Should().Be("Calix");
    }
}
