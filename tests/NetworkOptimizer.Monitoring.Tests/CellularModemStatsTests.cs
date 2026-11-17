using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class CellularModemStatsTests
{
    #region PrimarySignal Tests

    [Fact]
    public void PrimarySignal_WithBothLteAndNr5g_Prefers5gWithData()
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24 },
            Nr5g = new SignalInfo { Rsrp = -85, Rsrq = -7, Snr = 28 }
        };

        stats.PrimarySignal.Should().Be(stats.Nr5g);
    }

    [Fact]
    public void PrimarySignal_WithEmptyNr5g_FallsBackToLte()
    {
        // This is the bug case - Nr5g object exists but has no data
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24 },
            Nr5g = new SignalInfo() // Empty, no RSRP
        };

        stats.PrimarySignal.Should().Be(stats.Lte);
    }

    [Fact]
    public void PrimarySignal_WithNullNr5g_UsesLte()
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24 },
            Nr5g = null
        };

        stats.PrimarySignal.Should().Be(stats.Lte);
    }

    [Fact]
    public void PrimarySignal_WithOnlyNr5gData_UsesNr5g()
    {
        // 5G Standalone scenario
        var stats = new CellularModemStats
        {
            Lte = null,
            Nr5g = new SignalInfo { Rsrp = -85, Rsrq = -7, Snr = 28 }
        };

        stats.PrimarySignal.Should().Be(stats.Nr5g);
    }

    [Fact]
    public void PrimarySignal_WithNoSignal_ReturnsNull()
    {
        var stats = new CellularModemStats
        {
            Lte = null,
            Nr5g = null
        };

        stats.PrimarySignal.Should().BeNull();
    }

    #endregion

    #region SignalQuality Tests - RSRP Only (LTE)

    [Theory]
    [InlineData(-80, 100)]  // Excellent (clamped)
    [InlineData(-90, 100)]  // Excellent (top of LTE range)
    [InlineData(-100, 66)]  // Good - (20 * 100/30 = 66.67 truncated)
    [InlineData(-110, 33)]  // Fair
    [InlineData(-120, 0)]   // Poor
    [InlineData(-70, 100)]  // Clamped to max
    [InlineData(-130, 0)]   // Clamped to min
    public void SignalQuality_LteWithRsrpOnly_CalculatesCorrectly(double rsrp, int expectedQuality)
    {
        // LTE uses range: -90 dBm (excellent) to -120 dBm (poor)
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = rsrp }
        };

        // With only RSRP, it gets 100% of the weight, so result should match
        stats.SignalQuality.Should().Be(expectedQuality);
    }

    #endregion

    #region SignalQuality Tests - RSRP Only (5G)

    [Theory]
    [InlineData(-70, 100)]  // Excellent (clamped)
    [InlineData(-80, 100)]  // Excellent (top of 5G range)
    [InlineData(-90, 66)]   // Good - (20 * 100/30 = 66.67 truncated)
    [InlineData(-100, 33)]  // Fair
    [InlineData(-110, 0)]   // Poor
    [InlineData(-120, 0)]   // Clamped to min
    public void SignalQuality_5gWithRsrpOnly_CalculatesCorrectly(double rsrp, int expectedQuality)
    {
        // 5G uses tighter range: -80 dBm (excellent) to -110 dBm (poor)
        var stats = new CellularModemStats
        {
            Nr5g = new SignalInfo { Rsrp = rsrp }
        };

        // With only RSRP, it gets 100% of the weight, so result should match
        stats.SignalQuality.Should().Be(expectedQuality);
    }

    [Fact]
    public void SignalQuality_SameRsrp_5gScoresLowerThanLte()
    {
        // At -100 dBm, LTE should score higher than 5G because
        // -100 is "good" for LTE but "fair" for 5G
        var lteStats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -100 }
        };

        var nr5gStats = new CellularModemStats
        {
            Nr5g = new SignalInfo { Rsrp = -100 }
        };

        lteStats.SignalQuality.Should().BeGreaterThan(nr5gStats.SignalQuality);
        lteStats.SignalQuality.Should().Be(66);  // LTE: (-100+120)*(100/30) = 66.67 truncated
        nr5gStats.SignalQuality.Should().Be(33); // 5G: (-100+110)*(100/30) = 33.33 truncated
    }

    #endregion

    #region SignalQuality Tests - All Metrics

    [Fact]
    public void SignalQuality_LteWithAllMetrics_CalculatesWeightedScore()
    {
        // User's actual scenario: RSRP -92, RSRQ -9, SNR 24.6
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24.6 }
        };

        // LTE RSRP: (-92 + 120) * (100/30) = 93.3, weight 0.5 -> 46.7
        // SNR: 24.6 * (100/30) = 82, weight 0.3 -> 24.6
        // RSRQ: (-9 + 20) * (100/17) = 64.7, weight 0.2 -> 12.9
        // Total = 46.7 + 24.6 + 12.9 = 84.2 -> 84
        stats.SignalQuality.Should().BeInRange(83, 85);
    }

    [Fact]
    public void SignalQuality_5gWithAllMetrics_CalculatesWeightedScore()
    {
        // 5G scenario with same metrics
        var stats = new CellularModemStats
        {
            Nr5g = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24.6 }
        };

        // 5G RSRP: (-92 + 110) * (100/30) = 60, weight 0.5 -> 30
        // SNR: 24.6 * (100/30) = 82, weight 0.3 -> 24.6
        // RSRQ: (-9 + 20) * (100/17) = 64.7, weight 0.2 -> 12.9
        // Total = 30 + 24.6 + 12.9 = 67.5 -> 67-68
        stats.SignalQuality.Should().BeInRange(67, 69);
    }

    [Fact]
    public void SignalQuality_WithExcellentSignal_ReturnsHigh()
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -75, Rsrq = -5, Snr = 30 }
        };

        // All metrics at excellent levels should give ~100
        stats.SignalQuality.Should().BeInRange(95, 100);
    }

    [Fact]
    public void SignalQuality_WithPoorSignal_ReturnsLow()
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -115, Rsrq = -18, Snr = 5 }
        };

        // All metrics at poor levels should give low score
        stats.SignalQuality.Should().BeLessThan(20);
    }

    [Fact]
    public void SignalQuality_WithEmptyNr5g_UsesLteMetrics()
    {
        // The original bug - empty Nr5g object was being picked over valid Lte
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24 },
            Nr5g = new SignalInfo() // Empty object with no RSRP
        };

        // Should NOT return 0 (no signal), should use LTE data
        // LTE RSRP at -92 with new formula gives ~84%
        stats.SignalQuality.Should().BeGreaterThan(50);
        stats.SignalQuality.Should().BeInRange(82, 86);
    }

    [Fact]
    public void SignalQuality_WithNoSignal_ReturnsZero()
    {
        var stats = new CellularModemStats
        {
            Lte = null,
            Nr5g = null
        };

        stats.SignalQuality.Should().Be(0);
    }

    #endregion

    #region NetworkMode Tests

    [Fact]
    public void NetworkMode_WithLteOnly_ReturnsLte()
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92 },
            Nr5g = null
        };

        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
    }

    [Fact]
    public void NetworkMode_WithEmptyNr5g_ReturnsLte()
    {
        // Empty Nr5g object should be treated as no 5G
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92 },
            Nr5g = new SignalInfo() // No RSRP
        };

        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
    }

    [Fact]
    public void NetworkMode_WithBothLteAndNr5g_ReturnsNsa()
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92 },
            Nr5g = new SignalInfo { Rsrp = -85 }
        };

        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gNsa);
    }

    [Fact]
    public void NetworkMode_WithNr5gOnly_ReturnsSa()
    {
        var stats = new CellularModemStats
        {
            Lte = null,
            Nr5g = new SignalInfo { Rsrp = -85 }
        };

        stats.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
    }

    #endregion

    #region SignalInfo Bars Tests

    [Theory]
    [InlineData(-75, 5)]   // Excellent
    [InlineData(-85, 4)]   // Good
    [InlineData(-95, 3)]   // Fair
    [InlineData(-105, 2)]  // Poor
    [InlineData(-115, 1)]  // Very poor
    [InlineData(-125, 0)]  // No signal
    public void SignalInfo_Bars_CalculatesCorrectly(double rsrp, int expectedBars)
    {
        var signal = new SignalInfo { Rsrp = rsrp };
        signal.Bars.Should().Be(expectedBars);
    }

    [Fact]
    public void SignalInfo_Bars_WithNoRsrp_ReturnsZero()
    {
        var signal = new SignalInfo();
        signal.Bars.Should().Be(0);
    }

    #endregion

    #region Signal Quality Steps

    [Theory]
    [InlineData(100, 5, "signal-excellent", "#10b981")]
    [InlineData(80, 5, "signal-excellent", "#10b981")]
    [InlineData(79, 4, "signal-good", "#22c55e")]
    [InlineData(60, 4, "signal-good", "#22c55e")]
    [InlineData(59, 3, "signal-fair", "#eab308")]
    [InlineData(40, 3, "signal-fair", "#eab308")]
    [InlineData(39, 2, "signal-weak", "#f97316")]
    [InlineData(20, 2, "signal-weak", "#f97316")]
    [InlineData(19, 1, "signal-poor", "#ef4444")]
    [InlineData(1, 1, "signal-poor", "#ef4444")]
    [InlineData(0, 0, "signal-poor", "#ef4444")]
    public void Quality_StepsEveryTwentyPoints(int quality, int bars, string cls, string color)
    {
        CellularModemStats.QualityBars(quality).Should().Be(bars);
        CellularModemStats.QualityClass(quality).Should().Be(cls);
        CellularModemStats.QualityColor(quality).Should().Be(color);
    }

    #endregion
}
