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

    #region SignalQuality Tests - RSRP Only

    [Theory]
    [InlineData(-80, 100)]  // Excellent
    [InlineData(-90, 75)]   // Good
    [InlineData(-100, 50)]  // Fair
    [InlineData(-110, 25)]  // Poor
    [InlineData(-120, 0)]   // Very poor
    [InlineData(-70, 100)]  // Clamped to max
    [InlineData(-130, 0)]   // Clamped to min
    public void SignalQuality_WithRsrpOnly_CalculatesCorrectly(double rsrp, int expectedQuality)
    {
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = rsrp }
        };

        // With only RSRP, it gets 100% of the weight, so result should match
        stats.SignalQuality.Should().Be(expectedQuality);
    }

    #endregion

    #region SignalQuality Tests - All Metrics

    [Fact]
    public void SignalQuality_WithAllMetrics_CalculatesWeightedScore()
    {
        // User's actual scenario: RSRP -92, RSRQ -9, SNR 24.6
        var stats = new CellularModemStats
        {
            Lte = new SignalInfo { Rsrp = -92, Rsrq = -9, Snr = 24.6 }
        };

        // RSRP: (-92 + 120) * 2.5 = 70, weight 0.5 -> 35
        // SNR: 24.6 * (100/30) = 82, weight 0.3 -> 24.6
        // RSRQ: (-9 + 20) * (100/17) = 64.7, weight 0.2 -> 12.9
        // Total = 35 + 24.6 + 12.9 = 72.5 -> 72
        stats.SignalQuality.Should().BeInRange(72, 73);
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

        // Should NOT return 50 (unknown), should use LTE data
        stats.SignalQuality.Should().BeGreaterThan(50);
        stats.SignalQuality.Should().BeInRange(70, 75);
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
}
