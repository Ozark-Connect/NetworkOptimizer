using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class SpeedtestIntegrationTests
{
    private static SqmConfiguration CreateConfig(
        int minDownloadSpeed = 190,
        int maxDownloadSpeed = 285,
        double overheadMultiplier = 1.05,
        string iface = "eth2")
    {
        return new SqmConfiguration
        {
            MinDownloadSpeed = minDownloadSpeed,
            MaxDownloadSpeed = maxDownloadSpeed,
            OverheadMultiplier = overheadMultiplier,
            Interface = iface
        };
    }

    #region ParseSpeedtestJson Tests

    [Fact]
    public void ParseSpeedtestJson_ValidJson_ReturnsResult()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var json = @"{
            ""timestamp"": ""2026-01-13T10:00:00Z"",
            ""ping"": { ""latency"": 15.5 },
            ""download"": { ""bandwidth"": 35000000 },
            ""upload"": { ""bandwidth"": 5000000 }
        }";

        // Act
        var result = integration.ParseSpeedtestJson(json);

        // Assert
        result.Should().NotBeNull();
        result!.Ping.Latency.Should().Be(15.5);
        result.Download.Bandwidth.Should().Be(35000000);
        result.Upload.Bandwidth.Should().Be(5000000);
    }

    [Fact]
    public void ParseSpeedtestJson_InvalidJson_ReturnsNull()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var json = "not valid json";

        // Act
        var result = integration.ParseSpeedtestJson(json);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParseSpeedtestJson_EmptyJson_ReturnsNull()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());

        // Act
        var result = integration.ParseSpeedtestJson("");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region BytesPerSecToMbps Tests

    [Theory]
    [InlineData(0, 0)]
    [InlineData(125000, 1)] // 1 Mbps = 125000 bytes/sec
    [InlineData(12500000, 100)] // 100 Mbps
    [InlineData(125000000, 1000)] // 1 Gbps
    [InlineData(35000000, 280)] // ~280 Mbps
    public void BytesPerSecToMbps_VariousValues_ConvertsCorrectly(long bytesPerSec, double expectedMbps)
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());

        // Act
        var result = integration.BytesPerSecToMbps(bytesPerSec);

        // Assert
        result.Should().Be(expectedMbps);
    }

    #endregion

    #region CalculateEffectiveRate Tests

    [Fact]
    public void CalculateEffectiveRate_AppliesOverheadMultiplier()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.05);
        var integration = new SpeedtestIntegration(config);

        // Act
        var result = integration.CalculateEffectiveRate(200);

        // Assert
        result.Should().Be(210); // 200 * 1.05 = 210, rounded
    }

    [Fact]
    public void CalculateEffectiveRate_EnforcesMinimum()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.0);
        var integration = new SpeedtestIntegration(config);

        // Act
        var result = integration.CalculateEffectiveRate(50);

        // Assert
        result.Should().Be(100); // Enforced minimum
    }

    [Fact]
    public void CalculateEffectiveRate_EnforcesMaximum()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.1);
        var integration = new SpeedtestIntegration(config);

        // Act
        var result = integration.CalculateEffectiveRate(350);

        // Assert
        result.Should().Be(300); // Enforced maximum (350 * 1.1 = 385, capped at 300)
    }

    [Fact]
    public void CalculateEffectiveRate_RoundsToWholeNumber()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.05);
        var integration = new SpeedtestIntegration(config);

        // Act
        var result = integration.CalculateEffectiveRate(111.11);

        // Assert
        result.Should().Be(117); // 111.11 * 1.05 = 116.6655 → 117
    }

    #endregion

    #region ProcessSpeedtestResult Tests

    [Fact]
    public void ProcessSpeedtestResult_WithoutBaseline_UsesMeasuredSpeed()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.0);
        var integration = new SpeedtestIntegration(config);
        var baselineCalculator = new BaselineCalculator();
        var result = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 25000000 }, // 200 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 },
            Timestamp = DateTime.Now
        };

        // Act
        var effectiveRate = integration.ProcessSpeedtestResult(result, baselineCalculator);

        // Assert
        effectiveRate.Should().Be(200); // 200 Mbps with 1.0 multiplier, no baseline blending
    }

    [Fact]
    public void ProcessSpeedtestResult_WithBaseline_BlendsSpeed()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.0);
        var integration = new SpeedtestIntegration(config);
        var baselineCalculator = new BaselineCalculator();

        // Set up baseline for current hour
        var now = DateTime.Now;
        var dayOfWeek = now.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)now.DayOfWeek - 1;
        baselineCalculator.LoadBaselineTable(new BaselineTable
        {
            Baselines = { [$"{dayOfWeek}_{now.Hour}"] = new HourlyBaseline { Median = 250, Mean = 250 } }
        });

        var result = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 25000000 }, // 200 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 },
            Timestamp = now
        };

        // Act
        var effectiveRate = integration.ProcessSpeedtestResult(result, baselineCalculator);

        // Assert - Blended result should be between measured and baseline
        effectiveRate.Should().BeGreaterThan(200);
        effectiveRate.Should().BeLessThan(250);
    }

    [Fact]
    public void ProcessSpeedtestResult_Applies95PercentSafetyCap()
    {
        // Arrange
        var config = CreateConfig(minDownloadSpeed: 100, maxDownloadSpeed: 300, overheadMultiplier: 1.0);
        var integration = new SpeedtestIntegration(config);
        var baselineCalculator = new BaselineCalculator();
        var result = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 40000000 }, // 320 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 },
            Timestamp = DateTime.Now
        };

        // Act
        var effectiveRate = integration.ProcessSpeedtestResult(result, baselineCalculator);

        // Assert - Should be capped at 95% of maxDownloadSpeed (285)
        effectiveRate.Should().BeLessThanOrEqualTo(285);
    }

    #endregion

    #region CreateSample Tests

    [Fact]
    public void CreateSample_CreatesCorrectSample()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var result = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 25000000 }, // 200 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 }, // 40 Mbps
            Ping = new PingInfo { Latency = 15.5 },
            Timestamp = new DateTime(2026, 1, 13, 14, 30, 0)
        };

        // Act
        var sample = integration.CreateSample(result);

        // Assert
        sample.DownloadSpeed.Should().Be(200);
        sample.UploadSpeed.Should().Be(40);
        sample.Latency.Should().Be(15.5);
        sample.Timestamp.Should().Be(result.Timestamp);
    }

    #endregion

    #region IsValidResult Tests

    [Fact]
    public void IsValidResult_NullResult_ReturnsFalse()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());

        // Act
        var result = integration.IsValidResult(null!);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidResult_ZeroDownload_ReturnsFalse()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var speedtest = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 0 },
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 }
        };

        // Act
        var result = integration.IsValidResult(speedtest);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidResult_ZeroUpload_ReturnsFalse()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var speedtest = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 25000000 },
            Upload = new BandwidthInfo { Bandwidth = 0 },
            Ping = new PingInfo { Latency = 15 }
        };

        // Act
        var result = integration.IsValidResult(speedtest);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidResult_ZeroLatency_ReturnsFalse()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var speedtest = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 25000000 },
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 0 }
        };

        // Act
        var result = integration.IsValidResult(speedtest);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidResult_UnreasonablyLowSpeed_ReturnsFalse()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var speedtest = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 100000 }, // ~0.8 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 }
        };

        // Act
        var result = integration.IsValidResult(speedtest);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidResult_UnreasonablyHighSpeed_ReturnsFalse()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var speedtest = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 1500000000000 }, // 12000000 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 }
        };

        // Act
        var result = integration.IsValidResult(speedtest);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsValidResult_ValidResult_ReturnsTrue()
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());
        var speedtest = new SpeedtestResult
        {
            Download = new BandwidthInfo { Bandwidth = 25000000 }, // 200 Mbps
            Upload = new BandwidthInfo { Bandwidth = 5000000 },
            Ping = new PingInfo { Latency = 15 }
        };

        // Act
        var result = integration.IsValidResult(speedtest);

        // Assert
        result.Should().BeTrue();
    }

    #endregion

    #region GenerateSpeedtestCommand Tests

    [Fact]
    public void GenerateSpeedtestCommand_GeneratesCorrectCommand()
    {
        // Arrange
        var config = CreateConfig(iface: "eth4");
        var integration = new SpeedtestIntegration(config);

        // Act
        var command = integration.GenerateSpeedtestCommand();

        // Assert
        command.Should().Contain("speedtest");
        command.Should().Contain("--accept-license");
        command.Should().Contain("--format=json");
        command.Should().Contain("--interface=eth4");
    }

    #endregion

    #region CalculateVariancePercent Tests

    [Theory]
    [InlineData(100, 100, 0)]    // No variance
    [InlineData(110, 100, 10)]   // 10% above
    [InlineData(90, 100, -10)]   // 10% below
    [InlineData(150, 100, 50)]   // 50% above
    [InlineData(50, 100, -50)]   // 50% below
    [InlineData(100, 0, 0)]      // Division by zero protection
    public void CalculateVariancePercent_VariousValues_ReturnsCorrectPercentage(
        double measured, double baseline, double expected)
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());

        // Act
        var result = integration.CalculateVariancePercent(measured, baseline);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region DetermineBlendRatio Tests

    [Theory]
    [InlineData(-5, 0.6, 0.4)]   // 5% below - within threshold
    [InlineData(-10, 0.6, 0.4)]  // 10% below - within threshold
    [InlineData(0, 0.6, 0.4)]    // At baseline
    [InlineData(10, 0.6, 0.4)]   // 10% above
    [InlineData(-15, 0.8, 0.2)]  // 15% below - below threshold
    [InlineData(-20, 0.8, 0.2)]  // 20% below - below threshold
    public void DetermineBlendRatio_VariousVariances_ReturnsCorrectRatio(
        double variancePercent, double expectedBaselineWeight, double expectedMeasuredWeight)
    {
        // Arrange
        var integration = new SpeedtestIntegration(CreateConfig());

        // Act
        var (baselineWeight, measuredWeight) = integration.DetermineBlendRatio(variancePercent);

        // Assert
        baselineWeight.Should().Be(expectedBaselineWeight);
        measuredWeight.Should().Be(expectedMeasuredWeight);
    }

    #endregion
}
