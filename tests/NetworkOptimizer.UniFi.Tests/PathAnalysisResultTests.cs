using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class PathAnalysisResultTests
{
    #region Default Values Tests

    [Fact]
    public void PathAnalysisResult_DefaultValues_AreCorrect()
    {
        // Act
        var result = new PathAnalysisResult();

        // Assert
        result.Path.Should().NotBeNull();
        result.MeasuredFromDeviceMbps.Should().Be(0);
        result.MeasuredToDeviceMbps.Should().Be(0);
        result.FromDeviceRetransmits.Should().Be(0);
        result.ToDeviceRetransmits.Should().Be(0);
        result.FromDeviceBytes.Should().Be(0);
        result.ToDeviceBytes.Should().Be(0);
        result.FromDeviceEfficiencyPercent.Should().Be(0);
        result.ToDeviceEfficiencyPercent.Should().Be(0);
        result.FromDeviceGrade.Should().Be(PerformanceGrade.Excellent);  // Default enum value
        result.ToDeviceGrade.Should().Be(PerformanceGrade.Excellent);
        result.Insights.Should().NotBeNull().And.BeEmpty();
        result.Recommendations.Should().NotBeNull().And.BeEmpty();
    }

    #endregion

    #region CalculateEfficiency Tests

    [Fact]
    public void CalculateEfficiency_ZeroRealisticMax_DoesNotCalculate()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 0 },
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 500
        };

        // Act
        result.CalculateEfficiency();

        // Assert - Should remain at defaults
        result.FromDeviceEfficiencyPercent.Should().Be(0);
        result.ToDeviceEfficiencyPercent.Should().Be(0);
    }

    [Theory]
    [InlineData(940, 1000, 94)]   // 94% efficiency
    [InlineData(500, 1000, 50)]   // 50% efficiency
    [InlineData(250, 1000, 25)]   // 25% efficiency
    [InlineData(100, 1000, 10)]   // 10% efficiency
    public void CalculateEfficiency_CalculatesCorrectPercentage(double measured, int maxMbps, double expectedPercent)
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = maxMbps },
            MeasuredFromDeviceMbps = measured,
            MeasuredToDeviceMbps = measured
        };

        // Act
        result.CalculateEfficiency();

        // Assert
        result.FromDeviceEfficiencyPercent.Should().BeApproximately(expectedPercent, 0.1);
        result.ToDeviceEfficiencyPercent.Should().BeApproximately(expectedPercent, 0.1);
    }

    [Theory]
    [InlineData(95, PerformanceGrade.Excellent)]
    [InlineData(90, PerformanceGrade.Excellent)]
    [InlineData(89, PerformanceGrade.Good)]
    [InlineData(75, PerformanceGrade.Good)]
    [InlineData(74, PerformanceGrade.Fair)]
    [InlineData(50, PerformanceGrade.Fair)]
    [InlineData(49, PerformanceGrade.Poor)]
    [InlineData(25, PerformanceGrade.Poor)]
    [InlineData(24, PerformanceGrade.Critical)]
    [InlineData(10, PerformanceGrade.Critical)]
    [InlineData(0, PerformanceGrade.Critical)]
    public void CalculateEfficiency_AssignsCorrectGrade(double efficiencyPercent, PerformanceGrade expectedGrade)
    {
        // Arrange - Calculate measured speed to get desired efficiency
        var maxMbps = 1000;
        var measured = efficiencyPercent * maxMbps / 100.0;

        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = maxMbps },
            MeasuredFromDeviceMbps = measured,
            MeasuredToDeviceMbps = measured
        };

        // Act
        result.CalculateEfficiency();

        // Assert
        result.FromDeviceGrade.Should().Be(expectedGrade);
        result.ToDeviceGrade.Should().Be(expectedGrade);
    }

    [Fact]
    public void CalculateEfficiency_AsymmetricSpeeds_CalculatesBothCorrectly()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = 900,  // 90% - Excellent
            MeasuredToDeviceMbps = 500     // 50% - Fair
        };

        // Act
        result.CalculateEfficiency();

        // Assert
        result.FromDeviceEfficiencyPercent.Should().Be(90);
        result.FromDeviceGrade.Should().Be(PerformanceGrade.Excellent);
        result.ToDeviceEfficiencyPercent.Should().Be(50);
        result.ToDeviceGrade.Should().Be(PerformanceGrade.Fair);
    }

    #endregion

    #region GenerateInsights Tests - Gateway Tests

    [Fact]
    public void GenerateInsights_GatewayTest_SkipsPerformanceWarnings()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TargetIsGateway = true,
                RealisticMaxMbps = 1000
            },
            MeasuredFromDeviceMbps = 100,  // Would be Critical grade
            MeasuredToDeviceMbps = 100
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().ContainSingle()
            .Which.Should().Contain("Gateway speed test");
        result.Recommendations.Should().BeEmpty();
    }

    #endregion

    #region GenerateInsights Tests - AP Tests

    [Fact]
    public void GenerateInsights_ApTestPerformingWell_NotesLimitedByCpu()
    {
        // Arrange - AP test above 4400 Mbps threshold
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TargetIsAccessPoint = true,
                RealisticMaxMbps = 10000
            },
            MeasuredFromDeviceMbps = 4500,
            MeasuredToDeviceMbps = 4500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().ContainSingle()
            .Which.Should().Contain("AP speed test - results limited by AP CPU");
    }

    [Fact]
    public void GenerateInsights_ApTestBelowThreshold_GeneratesNormalInsights()
    {
        // Arrange - AP test below 4400 Mbps threshold
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TargetIsAccessPoint = true,
                RealisticMaxMbps = 10000
            },
            MeasuredFromDeviceMbps = 3000,  // Below AP threshold
            MeasuredToDeviceMbps = 3000
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain(i => i.Contains("Performance below expected"));
    }

    #endregion

    #region GenerateInsights Tests - Wireless Connection

    [Fact]
    public void GenerateInsights_WirelessConnection_NotesVariableSpeed()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                RealisticMaxMbps = 1000,
                Hops = new List<NetworkHop>
                {
                    new() { Type = HopType.Client },
                    new() { Type = HopType.AccessPoint }
                }
            },
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 900
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain(i => i.Contains("wireless segment"));
    }

    #endregion

    #region GenerateInsights Tests - Performance Issues

    [Fact]
    public void GenerateInsights_PoorPerformance_AddsCongestionWarning()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = 200,  // 20% - Poor
            MeasuredToDeviceMbps = 200
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain(i => i.Contains("Performance below expected"));
    }

    [Fact]
    public void GenerateInsights_FairPerformance_NotesModeratePerformance()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = 600,  // 60% - Fair
            MeasuredToDeviceMbps = 600
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().Contain(i => i.Contains("Performance is moderate"));
    }

    [Fact]
    public void GenerateInsights_LargeAsymmetry_RecommendsDuplexCheck()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = 200,  // 20% - Poor
            MeasuredToDeviceMbps = 500     // 50% - Fair, >20% difference
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r => r.Contains("asymmetry"));
    }

    #endregion

    #region GenerateInsights Tests - Link Speed Recommendations

    [Fact]
    public void GenerateInsights_100MbpsLink_RecommendsUpgrade()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TheoreticalMaxMbps = 100,
                RealisticMaxMbps = 94
            },
            MeasuredFromDeviceMbps = 90,
            MeasuredToDeviceMbps = 90
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r => r.Contains("10/100 Mbps link detected"));
    }

    [Fact]
    public void GenerateInsights_WirelessWith100MbpsTheo_DoesNotRecommendUpgrade()
    {
        // Arrange - Wireless paths shouldn't trigger 10/100M wired cable warning
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TheoreticalMaxMbps = 100,
                RealisticMaxMbps = 94,
                Hops = new List<NetworkHop>
                {
                    new() { Type = HopType.Client },
                    new() { Type = HopType.AccessPoint }
                }
            },
            MeasuredFromDeviceMbps = 90,
            MeasuredToDeviceMbps = 90
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().NotContain(r => r.Contains("10/100 Mbps link detected"));
    }

    [Fact]
    public void GenerateInsights_MaxingOutGigabit_Recommends25GUpgrade()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                TheoreticalMaxMbps = 1000,
                RealisticMaxMbps = 940
            },
            MeasuredFromDeviceMbps = 900,  // 95% efficiency
            MeasuredToDeviceMbps = 900
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r => r.Contains("Maxing out 1 GbE"));
    }

    #endregion

    #region GenerateInsights Tests - Retransmits

    [Fact]
    public void GenerateInsights_NoRetransmits_NoRetransmitInsights()
    {
        // Arrange
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath { RealisticMaxMbps = 1000 },
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 900,
            FromDeviceBytes = 1_000_000_000,
            ToDeviceBytes = 1_000_000_000,
            FromDeviceRetransmits = 0,
            ToDeviceRetransmits = 0
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Insights.Should().NotContain(i => i.Contains("retransmit"));
        result.Insights.Should().NotContain(i => i.Contains("packet loss"));
    }

    [Fact]
    public void GenerateInsights_ElevatedRetransmits_WirelessClient_RecommendsSignalCheck()
    {
        // Arrange - Wireless client with retransmits
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                RealisticMaxMbps = 1000,
                Hops = new List<NetworkHop>
                {
                    new() { Type = HopType.Client },
                    new() { Type = HopType.AccessPoint }
                }
            },
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 900,
            FromDeviceBytes = 100_000_000,  // ~66,666 packets
            ToDeviceBytes = 100_000_000,
            FromDeviceRetransmits = 500,    // ~0.75% - elevated
            ToDeviceRetransmits = 500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r =>
            r.Contains("Wi-Fi") && r.Contains("signal strength"));
    }

    [Fact]
    public void GenerateInsights_ElevatedRetransmits_MeshedAp_RecommendsBackhaulCheck()
    {
        // Arrange - AP with wireless mesh backhaul
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                RealisticMaxMbps = 1000,
                TargetIsAccessPoint = true,
                Hops = new List<NetworkHop>
                {
                    new() { Type = HopType.AccessPoint },  // Target AP
                    new() { Type = HopType.AccessPoint }   // Mesh backhaul
                }
            },
            MeasuredFromDeviceMbps = 400,  // Lower due to mesh
            MeasuredToDeviceMbps = 400,
            FromDeviceBytes = 50_000_000,
            ToDeviceBytes = 50_000_000,
            FromDeviceRetransmits = 500,
            ToDeviceRetransmits = 500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r =>
            r.Contains("wireless mesh") || r.Contains("mesh backhaul"));
    }

    [Fact]
    public void GenerateInsights_UniFiDevice_HigherRetransmitThreshold()
    {
        // Arrange - UniFi AP should use higher thresholds
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                RealisticMaxMbps = 1000,
                TargetIsAccessPoint = true
            },
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 900,
            FromDeviceBytes = 100_000_000,  // ~66,666 packets
            ToDeviceBytes = 100_000_000,
            FromDeviceRetransmits = 400,    // ~0.6% - would be elevated for regular client, but OK for AP
            ToDeviceRetransmits = 400
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert - Should NOT trigger elevated warning for UniFi device at 0.6%
        result.Insights.Should().NotContain(i => i.Contains("Elevated packet loss"));
    }

    [Fact]
    public void GenerateInsights_BidirectionalRetransmits_WiredPath_RecommendsCableCheck()
    {
        // Arrange - Wired path with bidirectional retransmits
        var result = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                RealisticMaxMbps = 1000,
                Hops = new List<NetworkHop>
                {
                    new() { Type = HopType.Switch },
                    new() { Type = HopType.Switch }
                }
            },
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 500,
            FromDeviceBytes = 50_000_000,
            ToDeviceBytes = 50_000_000,
            FromDeviceRetransmits = 500,   // ~1.5%
            ToDeviceRetransmits = 500
        };
        result.CalculateEfficiency();

        // Act
        result.GenerateInsights();

        // Assert
        result.Recommendations.Should().Contain(r =>
            r.Contains("Bidirectional") && r.Contains("faulty cables"));
    }

    #endregion

    #region PerformanceGrade Enum Tests

    [Fact]
    public void PerformanceGrade_AllValuesAreDefined()
    {
        // Assert
        var values = Enum.GetValues<PerformanceGrade>();
        values.Should().Contain(PerformanceGrade.Excellent);
        values.Should().Contain(PerformanceGrade.Good);
        values.Should().Contain(PerformanceGrade.Fair);
        values.Should().Contain(PerformanceGrade.Poor);
        values.Should().Contain(PerformanceGrade.Critical);
    }

    [Fact]
    public void PerformanceGrade_OrderIsCorrect()
    {
        // Assert - Lower enum value = better grade
        ((int)PerformanceGrade.Excellent).Should().BeLessThan((int)PerformanceGrade.Good);
        ((int)PerformanceGrade.Good).Should().BeLessThan((int)PerformanceGrade.Fair);
        ((int)PerformanceGrade.Fair).Should().BeLessThan((int)PerformanceGrade.Poor);
        ((int)PerformanceGrade.Poor).Should().BeLessThan((int)PerformanceGrade.Critical);
    }

    #endregion
}
