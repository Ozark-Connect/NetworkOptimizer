using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class LatencyMonitorTests
{
    private static SqmConfiguration CreateConfig(
        double baselineLatency = 17.9,
        double latencyThreshold = 2.2,
        double latencyDecrease = 0.97,
        double latencyIncrease = 1.04,
        int absoluteMaxDownloadSpeed = 280,
        int maxDownloadSpeed = 285,
        string pingHost = "8.8.8.8",
        string iface = "eth2")
    {
        return new SqmConfiguration
        {
            BaselineLatency = baselineLatency,
            LatencyThreshold = latencyThreshold,
            LatencyDecrease = latencyDecrease,
            LatencyIncrease = latencyIncrease,
            AbsoluteMaxDownloadSpeed = absoluteMaxDownloadSpeed,
            MaxDownloadSpeed = maxDownloadSpeed,
            PingHost = pingHost,
            Interface = iface
        };
    }

    #region CalculateRateAdjustment Tests - High Latency

    [Fact]
    public void CalculateRateAdjustment_HighLatency_DecreasesRate()
    {
        // Arrange
        var config = CreateConfig();
        var monitor = new LatencyMonitor(config);
        double currentLatency = 25; // Above threshold (17.9 + 2.2 = 20.1)
        double currentRate = 280;

        // Act
        var (adjustedRate, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        adjustedRate.Should().BeLessThan(currentRate);
        reason.Should().Contain("High latency");
        reason.Should().Contain("decreased");
    }

    [Fact]
    public void CalculateRateAdjustment_HighLatency_CalculatesDeviationsCorrectly()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 10, latencyThreshold: 2);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 16; // 6ms above baseline, 3 deviations at 2ms threshold
        double currentRate = 280;

        // Act
        var (adjustedRate, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        reason.Should().Contain("3 deviations");
    }

    [Fact]
    public void CalculateRateAdjustment_HighLatency_EnforcesMinimumRate()
    {
        // Arrange
        var config = CreateConfig();
        var monitor = new LatencyMonitor(config);
        double currentLatency = 50; // Very high latency
        double currentRate = 200; // Already low

        // Act
        var (adjustedRate, _) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        adjustedRate.Should().BeGreaterThanOrEqualTo(180); // Minimum floor
    }

    #endregion

    #region CalculateRateAdjustment Tests - Low Latency

    [Fact]
    public void CalculateRateAdjustment_LowLatency_BelowLowerBound_AppliesDoubleIncrease()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 17; // Below baseline - 0.4 (17.6)
        double currentRate = 250; // Below 92% of 300 (276)

        // Act
        var (adjustedRate, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        adjustedRate.Should().BeGreaterThan(currentRate);
        reason.Should().Contain("Latency reduced");
        reason.Should().Contain("2x increase");
    }

    [Fact]
    public void CalculateRateAdjustment_LowLatency_NearMidBound_NormalizesToMid()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 17; // Below baseline - 0.4
        double currentRate = 280; // Between 92% (276) and 94% (282)

        // Act
        var (_, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        reason.Should().Contain("normalizing to optimal");
    }

    [Fact]
    public void CalculateRateAdjustment_LowLatency_AboveMidBound_KeepsCurrentRate()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 17;
        double currentRate = 285; // Above 94% of 300 (282)

        // Act
        var (_, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        reason.Should().Contain("keeping current rate");
    }

    #endregion

    #region CalculateRateAdjustment Tests - Normal Latency

    [Fact]
    public void CalculateRateAdjustment_NormalLatency_BelowLowerBound_AppliesIncrease()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 18.2; // Within 0.3ms of baseline
        double currentRate = 260; // Below 90% of 300 (270)

        // Act
        var (adjustedRate, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        adjustedRate.Should().BeGreaterThan(currentRate);
        reason.Should().Contain("Normal latency");
        reason.Should().Contain("applying increase");
    }

    [Fact]
    public void CalculateRateAdjustment_NormalLatency_NearMidBound_NormalizesToOptimal()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 18.2;
        double currentRate = 272; // Between 90% (270) and 92% (276)

        // Act
        var (_, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        reason.Should().Contain("normalizing to optimal");
    }

    [Fact]
    public void CalculateRateAdjustment_NormalLatency_AboveThreshold_MaintainsRate()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);
        double currentLatency = 18.2;
        double currentRate = 280; // Above 92%

        // Act
        var (_, reason) = monitor.CalculateRateAdjustment(currentLatency, currentRate);

        // Assert
        reason.Should().Contain("maintaining current rate");
    }

    #endregion

    #region IsLatencyHigh Tests

    [Theory]
    [InlineData(17.9, 2.2, 20.0, false)] // Just below threshold
    [InlineData(17.9, 2.2, 20.1, true)]  // At threshold
    [InlineData(17.9, 2.2, 25.0, true)]  // Above threshold
    [InlineData(10.0, 5.0, 14.9, false)] // Just below
    [InlineData(10.0, 5.0, 15.0, true)]  // At threshold
    public void IsLatencyHigh_VariousValues_ReturnsCorrectResult(
        double baselineLatency, double threshold, double currentLatency, bool expected)
    {
        // Arrange
        var config = CreateConfig(baselineLatency: baselineLatency, latencyThreshold: threshold);
        var monitor = new LatencyMonitor(config);

        // Act
        var result = monitor.IsLatencyHigh(currentLatency);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region CalculateDeviationCount Tests

    [Theory]
    [InlineData(10, 2, 12, 1)]  // 1 deviation
    [InlineData(10, 2, 14, 2)]  // 2 deviations
    [InlineData(10, 2, 15, 3)]  // 2.5 deviations rounds up to 3
    [InlineData(10, 2, 16, 3)]  // 3 deviations
    [InlineData(10, 2, 10, 0)]  // No deviation
    public void CalculateDeviationCount_VariousLatencies_ReturnsCorrectCount(
        double baseline, double threshold, double current, int expectedDeviations)
    {
        // Arrange
        var config = CreateConfig(baselineLatency: baseline, latencyThreshold: threshold);
        var monitor = new LatencyMonitor(config);

        // Act
        var result = monitor.CalculateDeviationCount(current);

        // Assert
        result.Should().Be(expectedDeviations);
    }

    #endregion

    #region GeneratePingCommand Tests

    [Fact]
    public void GeneratePingCommand_GeneratesCorrectCommand()
    {
        // Arrange
        var config = CreateConfig(pingHost: "8.8.8.8", iface: "eth4");
        var monitor = new LatencyMonitor(config);

        // Act
        var command = monitor.GeneratePingCommand();

        // Assert
        command.Should().Contain("-I eth4");
        command.Should().Contain("-c 20");
        command.Should().Contain("-i 0.25");
        command.Should().Contain("-q");
        command.Should().Contain("8.8.8.8");
    }

    #endregion

    #region ParsePingOutput Tests

    [Fact]
    public void ParsePingOutput_ValidOutput_ReturnsAverageLatency()
    {
        // Arrange
        var config = CreateConfig();
        var monitor = new LatencyMonitor(config);
        var pingOutput = @"PING 8.8.8.8 (8.8.8.8) 56(84) bytes of data.

--- 8.8.8.8 ping statistics ---
20 packets transmitted, 20 received, 0% packet loss, time 4847ms
rtt min/avg/max/mdev = 10.123/12.456/15.789/2.345 ms";

        // Act
        var result = monitor.ParsePingOutput(pingOutput);

        // Assert
        result.Should().Be(12.456);
    }

    [Fact]
    public void ParsePingOutput_NoRttLine_ReturnsNull()
    {
        // Arrange
        var config = CreateConfig();
        var monitor = new LatencyMonitor(config);
        var pingOutput = @"PING 8.8.8.8 (8.8.8.8) 56(84) bytes of data.
Some other output without rtt";

        // Act
        var result = monitor.ParsePingOutput(pingOutput);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParsePingOutput_MalformedRtt_ReturnsNull()
    {
        // Arrange
        var config = CreateConfig();
        var monitor = new LatencyMonitor(config);
        var pingOutput = @"rtt min/avg/max/mdev = invalid";

        // Act
        var result = monitor.ParsePingOutput(pingOutput);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void ParsePingOutput_EmptyOutput_ReturnsNull()
    {
        // Arrange
        var config = CreateConfig();
        var monitor = new LatencyMonitor(config);

        // Act
        var result = monitor.ParsePingOutput("");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CalculateDecreaseMultiplier Tests

    [Theory]
    [InlineData(0.97, 1, 0.97)]
    [InlineData(0.97, 2, 0.9409)] // 0.97^2
    [InlineData(0.97, 3, 0.912673)] // 0.97^3
    [InlineData(0.95, 2, 0.9025)] // 0.95^2
    public void CalculateDecreaseMultiplier_VariousDeviations_ReturnsCorrectMultiplier(
        double decreaseRate, int deviations, double expected)
    {
        // Arrange
        var config = CreateConfig(latencyDecrease: decreaseRate);
        var monitor = new LatencyMonitor(config);

        // Act
        var result = monitor.CalculateDecreaseMultiplier(deviations);

        // Assert
        result.Should().BeApproximately(expected, 0.0001);
    }

    #endregion

    #region CalculateIncreaseMultiplier Tests

    [Theory]
    [InlineData(1.04, 1, 1.04)]
    [InlineData(1.04, 2, 1.0816)] // 1.04^2
    [InlineData(1.05, 2, 1.1025)] // 1.05^2
    public void CalculateIncreaseMultiplier_VariousSteps_ReturnsCorrectMultiplier(
        double increaseRate, int steps, double expected)
    {
        // Arrange
        var config = CreateConfig(latencyIncrease: increaseRate);
        var monitor = new LatencyMonitor(config);

        // Act
        var result = monitor.CalculateIncreaseMultiplier(steps);

        // Assert
        result.Should().BeApproximately(expected, 0.0001);
    }

    #endregion

    #region NeedsRecovery Tests

    [Theory]
    [InlineData(300, 250, true)]  // 250 < 276 (92% of 300)
    [InlineData(300, 276, false)] // At threshold
    [InlineData(300, 280, false)] // Above threshold
    [InlineData(100, 90, true)]   // 90 < 92 (92% of 100)
    public void NeedsRecovery_VariousRates_ReturnsCorrectResult(
        int absoluteMax, double currentRate, bool expected)
    {
        // Arrange
        var config = CreateConfig(absoluteMaxDownloadSpeed: absoluteMax);
        var monitor = new LatencyMonitor(config);

        // Act
        var result = monitor.NeedsRecovery(currentRate);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region GetRateBounds Tests

    [Fact]
    public void GetRateBounds_ReturnsCorrectBounds()
    {
        // Arrange
        var config = CreateConfig(absoluteMaxDownloadSpeed: 300);
        var monitor = new LatencyMonitor(config);

        // Act
        var (minRate, optimalRate, maxRate) = monitor.GetRateBounds();

        // Assert
        minRate.Should().Be(180);
        optimalRate.Should().Be(282); // 94% of 300
        maxRate.Should().Be(285); // 95% of 300
    }

    #endregion
}
