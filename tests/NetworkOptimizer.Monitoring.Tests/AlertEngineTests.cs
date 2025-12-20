using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Monitoring.Models;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class AlertEngineTests
{
    private readonly AlertEngine _engine;
    private readonly Mock<ILogger<AlertEngine>> _loggerMock;

    public AlertEngineTests()
    {
        _loggerMock = new Mock<ILogger<AlertEngine>>();
        _engine = new AlertEngine(_loggerMock.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        var act = () => new AlertEngine(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_InitializesDefaultThresholds()
    {
        var thresholds = _engine.GetThresholds();
        thresholds.Should().NotBeEmpty();
        thresholds.Should().Contain(t => t.Name.Contains("CPU"));
        thresholds.Should().Contain(t => t.Name.Contains("Memory"));
    }

    #endregion

    #region Threshold Management Tests

    [Fact]
    public void AddThreshold_AddsToCollection()
    {
        // Arrange
        var initialCount = _engine.GetThresholds().Count;
        var threshold = new AlertThreshold
        {
            Name = "Custom Threshold",
            MetricType = "device",
            MetricName = "CustomMetric",
            Value = 80,
            Comparison = ThresholdComparison.GreaterThan
        };

        // Act
        _engine.AddThreshold(threshold);

        // Assert
        _engine.GetThresholds().Should().HaveCount(initialCount + 1);
        _engine.GetThresholds().Should().Contain(t => t.Name == "Custom Threshold");
    }

    [Fact]
    public void AddThreshold_UpdatesExistingWithSameId()
    {
        // Arrange
        var id = Guid.NewGuid();
        var threshold1 = new AlertThreshold
        {
            Id = id,
            Name = "Original",
            Value = 80
        };
        var threshold2 = new AlertThreshold
        {
            Id = id,
            Name = "Updated",
            Value = 90
        };

        // Act
        _engine.AddThreshold(threshold1);
        var countAfterFirst = _engine.GetThresholds().Count;
        _engine.AddThreshold(threshold2);
        var countAfterSecond = _engine.GetThresholds().Count;

        // Assert
        countAfterSecond.Should().Be(countAfterFirst);
        _engine.GetThresholds().Should().Contain(t => t.Id == id && t.Name == "Updated" && t.Value == 90);
    }

    [Fact]
    public void AddThreshold_ThrowsOnNull()
    {
        var act = () => _engine.AddThreshold(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RemoveThreshold_RemovesFromCollection()
    {
        // Arrange
        var threshold = new AlertThreshold
        {
            Name = "To Remove",
            Value = 50
        };
        _engine.AddThreshold(threshold);
        var countBefore = _engine.GetThresholds().Count;

        // Act
        _engine.RemoveThreshold(threshold.Id);

        // Assert
        _engine.GetThresholds().Should().HaveCount(countBefore - 1);
        _engine.GetThresholds().Should().NotContain(t => t.Id == threshold.Id);
    }

    [Fact]
    public void RemoveThreshold_NonExistentId_DoesNotThrow()
    {
        // Act
        var act = () => _engine.RemoveThreshold(Guid.NewGuid());

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Alert Management Tests

    [Fact]
    public void GetActiveAlerts_InitiallyEmpty()
    {
        _engine.GetActiveAlerts().Should().BeEmpty();
    }

    [Fact]
    public void GetAlertHistory_InitiallyEmpty()
    {
        _engine.GetAlertHistory().Should().BeEmpty();
    }

    [Fact]
    public void AcknowledgeAlert_UpdatesAlertStatus()
    {
        // Arrange - First we need to create an alert
        // Add a threshold with no duration requirement
        var threshold = new AlertThreshold
        {
            Name = "Test Threshold",
            MetricType = "device",
            MetricName = "CpuUsage",
            Value = 50,
            Comparison = ThresholdComparison.GreaterThan,
            DurationSeconds = 0, // Immediate trigger
            CooldownSeconds = 0
        };
        _engine.AddThreshold(threshold);

        var metrics = new DeviceMetrics
        {
            IpAddress = "192.168.1.1",
            Hostname = "TestDevice",
            CpuUsage = 80
        };

        // Trigger alert
        _engine.EvaluateDeviceMetrics(metrics);
        var activeAlerts = _engine.GetActiveAlerts();
        activeAlerts.Should().NotBeEmpty();
        var alertId = activeAlerts.First().Id;

        // Act
        _engine.AcknowledgeAlert(alertId, "TestUser");

        // Assert
        var alert = _engine.GetAlertHistory().First(a => a.Id == alertId);
        alert.IsAcknowledged.Should().BeTrue();
        alert.AcknowledgedBy.Should().Be("TestUser");
        alert.Status.Should().Be(AlertStatus.Acknowledged);
    }

    [Fact]
    public void ResolveAlert_UpdatesAlertStatus()
    {
        // Arrange - Create an alert
        var threshold = new AlertThreshold
        {
            Name = "Test Threshold",
            MetricType = "device",
            MetricName = "CpuUsage",
            Value = 50,
            Comparison = ThresholdComparison.GreaterThan,
            DurationSeconds = 0,
            CooldownSeconds = 0
        };
        _engine.AddThreshold(threshold);

        var metrics = new DeviceMetrics
        {
            IpAddress = "192.168.1.1",
            Hostname = "TestDevice",
            CpuUsage = 80
        };

        _engine.EvaluateDeviceMetrics(metrics);
        var alertId = _engine.GetActiveAlerts().First().Id;

        // Act
        _engine.ResolveAlert(alertId);

        // Assert
        var alert = _engine.GetAlertHistory().First(a => a.Id == alertId);
        alert.Status.Should().Be(AlertStatus.Resolved);
        alert.ResolvedAt.Should().NotBeNull();
    }

    [Fact]
    public void ClearOldAlerts_RemovesResolvedOldAlerts()
    {
        // Arrange - Create and resolve an alert
        var threshold = new AlertThreshold
        {
            Name = "Test Threshold",
            MetricType = "device",
            MetricName = "CpuUsage",
            Value = 50,
            Comparison = ThresholdComparison.GreaterThan,
            DurationSeconds = 0,
            CooldownSeconds = 0
        };
        _engine.AddThreshold(threshold);

        var metrics = new DeviceMetrics
        {
            IpAddress = "192.168.1.1",
            Hostname = "TestDevice",
            CpuUsage = 80
        };

        _engine.EvaluateDeviceMetrics(metrics);
        var alertId = _engine.GetActiveAlerts().First().Id;
        _engine.ResolveAlert(alertId);

        // Act
        _engine.ClearOldAlerts(TimeSpan.Zero); // Clear all resolved alerts

        // Assert
        _engine.GetAlertHistory().Should().NotContain(a => a.Id == alertId);
    }

    #endregion

    #region Metric Evaluation Tests

    [Fact]
    public void EvaluateDeviceMetrics_ThrowsOnNull()
    {
        var act = () => _engine.EvaluateDeviceMetrics(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EvaluateInterfaceMetrics_ThrowsOnNull()
    {
        var act = () => _engine.EvaluateInterfaceMetrics(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EvaluateDeviceMetrics_BelowThreshold_ReturnsEmpty()
    {
        // Arrange
        var metrics = new DeviceMetrics
        {
            IpAddress = "192.168.1.1",
            Hostname = "TestDevice",
            CpuUsage = 50, // Below default threshold
            MemoryUsage = 50
        };

        // Act
        var alerts = _engine.EvaluateDeviceMetrics(metrics);

        // Assert
        alerts.Should().BeEmpty();
    }

    #endregion
}
