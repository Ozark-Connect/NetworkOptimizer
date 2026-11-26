using NetworkOptimizer.WiFi.Data;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ApModelNamesTests
{
    [Theory]
    [InlineData("U7-Pro-XG-B", "U7-Pro-XG")]
    [InlineData("U7-Pro-XGS-b", "U7-Pro-XGS")]
    [InlineData("UWB-XG-BK", "UWB-XG")]
    [InlineData("UWB-XG-bk", "UWB-XG")]
    [InlineData("UWB-XG", "UWB-XG")]
    [InlineData("U7-Mesh-Wall", "U7-Mesh-Wall")]
    [InlineData("UAP-AC-M", "UAP-AC-M")]
    [InlineData("", "")]
    public void StripColorSuffix_RemovesBlackSuffixOnly(string model, string expected)
    {
        Assert.Equal(expected, ApModelNames.StripColorSuffix(model));
    }

    [Fact]
    public void TryGetBandDefaults_BlackBaseStation_UsesBaseModel()
    {
        Assert.True(ApModelCatalog.TryGetBandDefaults("UWB-XG", "5", out var baseDefaults));
        Assert.True(ApModelCatalog.TryGetBandDefaults("UWB-XG-BK", "5", out var blackDefaults));
        Assert.Same(baseDefaults, blackDefaults);
    }

    [Fact]
    public void GetDefaultMountType_U7Industrial_IsWall()
    {
        Assert.Equal("wall", MountTypeHelper.GetDefaultMountType("U7-Industrial"));
    }

    [Fact]
    public void GetDefaultMountType_BlackVariant_MatchesBase()
    {
        Assert.Equal(MountTypeHelper.GetDefaultMountType("UWB-XG"), MountTypeHelper.GetDefaultMountType("UWB-XG-BK"));
    }
}
