using NetworkOptimizer.Audit.Constants;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Constants;

public class DetectionConstantsTests
{
    [Fact]
    public void ConfidenceScores_AreInDescendingOrder()
    {
        // Verify confidence hierarchy is logical
        Assert.True(DetectionConstants.MaxConfidence >= DetectionConstants.ProtectCameraConfidence);
        Assert.True(DetectionConstants.ProtectCameraConfidence >= DetectionConstants.NameOverrideConfidence);
        Assert.True(DetectionConstants.NameOverrideConfidence >= DetectionConstants.AppleWatchConfidence);
        Assert.True(DetectionConstants.AppleWatchConfidence >= DetectionConstants.OuiHighConfidence);
        Assert.True(DetectionConstants.OuiHighConfidence >= DetectionConstants.VendorDefaultConfidence);
        Assert.True(DetectionConstants.VendorDefaultConfidence >= DetectionConstants.OuiMediumConfidence);
        Assert.True(DetectionConstants.OuiMediumConfidence >= DetectionConstants.OuiStandardConfidence);
        Assert.True(DetectionConstants.OuiStandardConfidence >= DetectionConstants.OuiLowerConfidence);
        Assert.True(DetectionConstants.OuiLowerConfidence >= DetectionConstants.OuiLowestConfidence);
    }

    [Fact]
    public void MaxConfidence_Is100()
    {
        Assert.Equal(100, DetectionConstants.MaxConfidence);
    }

    [Fact]
    public void ProtectCameraConfidence_Is100()
    {
        // UniFi Protect cameras have highest confidence
        Assert.Equal(100, DetectionConstants.ProtectCameraConfidence);
    }

    [Fact]
    public void HistoricalClientWindow_Is14Days()
    {
        Assert.Equal(TimeSpan.FromDays(14), DetectionConstants.HistoricalClientWindow);
    }

    [Fact]
    public void OfflineThreshold_Is30Days()
    {
        Assert.Equal(TimeSpan.FromDays(30), DetectionConstants.OfflineThreshold);
    }

    [Fact]
    public void MultiSourceAgreementBoost_Is10()
    {
        Assert.Equal(10, DetectionConstants.MultiSourceAgreementBoost);
    }

    [Theory]
    [InlineData(nameof(DetectionConstants.OuiHighConfidence), 90)]
    [InlineData(nameof(DetectionConstants.OuiMediumConfidence), 85)]
    [InlineData(nameof(DetectionConstants.OuiStandardConfidence), 80)]
    [InlineData(nameof(DetectionConstants.OuiLowerConfidence), 75)]
    [InlineData(nameof(DetectionConstants.OuiLowestConfidence), 70)]
    public void OuiConfidenceLevels_HaveExpectedValues(string fieldName, int expectedValue)
    {
        var actualValue = fieldName switch
        {
            nameof(DetectionConstants.OuiHighConfidence) => DetectionConstants.OuiHighConfidence,
            nameof(DetectionConstants.OuiMediumConfidence) => DetectionConstants.OuiMediumConfidence,
            nameof(DetectionConstants.OuiStandardConfidence) => DetectionConstants.OuiStandardConfidence,
            nameof(DetectionConstants.OuiLowerConfidence) => DetectionConstants.OuiLowerConfidence,
            nameof(DetectionConstants.OuiLowestConfidence) => DetectionConstants.OuiLowestConfidence,
            _ => throw new ArgumentException($"Unknown field: {fieldName}")
        };
        Assert.Equal(expectedValue, actualValue);
    }
}
