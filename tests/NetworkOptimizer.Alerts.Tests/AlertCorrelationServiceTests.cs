using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

public class AlertCorrelationServiceTests
{
    private readonly AlertCorrelationService _service;
    private readonly Mock<IAlertRepository> _repositoryMock;

    public AlertCorrelationServiceTests()
    {
        _service = new AlertCorrelationService(NullLogger<AlertCorrelationService>.Instance);
        _repositoryMock = new Mock<IAlertRepository>();
    }

    private static AlertEvent CreateTestEvent(
        string eventType = "device.offline",
        string? deviceIp = "192.0.2.1",
        AlertSeverity severity = AlertSeverity.Warning)
    {
        return new AlertEvent
        {
            EventType = eventType,
            Severity = severity,
            Source = "device",
            Title = "Test alert",
            DeviceIp = deviceIp
        };
    }

    #region GetCorrelationKey

    [Fact]
    public void GetCorrelationKey_WithDeviceIp_ReturnsDeviceKey()
    {
        var evt = CreateTestEvent(deviceIp: "192.0.2.1");

        var key = _service.GetCorrelationKey(evt);

        key.Should().Be("device:192.0.2.1");
    }

    [Fact]
    public void GetCorrelationKey_WithoutDeviceIp_ReturnsSourceKey()
    {
        var evt = CreateTestEvent(deviceIp: null, eventType: "audit.score_dropped");

        var key = _service.GetCorrelationKey(evt);

        key.Should().Be("source:audit");
    }

    [Fact]
    public void GetCorrelationKey_NoDotInEventType_NoDeviceIp_ReturnsNull()
    {
        var evt = new AlertEvent
        {
            EventType = "simple",
            Source = "test",
            Title = "Test"
        };

        var key = _service.GetCorrelationKey(evt);

        key.Should().BeNull();
    }

    [Fact]
    public void GetCorrelationKey_DeviceIpTakesPriority_OverSourceKey()
    {
        var evt = CreateTestEvent(eventType: "audit.score_dropped", deviceIp: "192.0.2.1");

        var key = _service.GetCorrelationKey(evt);

        // Device IP key should take priority over source key
        key.Should().Be("device:192.0.2.1");
    }

    #endregion

    #region CorrelateAsync - Create New Incident

    [Fact]
    public async Task CorrelateAsync_NoExistingIncident_CreatesNew()
    {
        var evt = CreateTestEvent();
        var historyEntry = new AlertHistoryEntry { Id = 1 };

        _repositoryMock
            .Setup(r => r.GetActiveIncidentByKeyAsync("device:192.0.2.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AlertIncident?)null);

        _repositoryMock
            .Setup(r => r.SaveIncidentAsync(It.IsAny<AlertIncident>(), It.IsAny<CancellationToken>()))
            .Callback<AlertIncident, CancellationToken>((i, _) => i.Id = 42)
            .ReturnsAsync(42);

        var incident = await _service.CorrelateAsync(evt, historyEntry, _repositoryMock.Object);

        incident.Should().NotBeNull();
        incident!.AlertCount.Should().Be(1);
        incident.CorrelationKey.Should().Be("device:192.0.2.1");
        historyEntry.IncidentId.Should().Be(42);

        _repositoryMock.Verify(r => r.SaveIncidentAsync(It.IsAny<AlertIncident>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CorrelateAsync_NullCorrelationKey_ReturnsNull()
    {
        var evt = new AlertEvent
        {
            EventType = "simple",
            Source = "test",
            Title = "Test"
        };
        var historyEntry = new AlertHistoryEntry { Id = 1 };

        var incident = await _service.CorrelateAsync(evt, historyEntry, _repositoryMock.Object);

        incident.Should().BeNull();
        _repositoryMock.Verify(r => r.GetActiveIncidentByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region CorrelateAsync - Existing Incident

    [Fact]
    public async Task CorrelateAsync_ExistingIncidentWithinWindow_AddsToIt()
    {
        var evt = CreateTestEvent(severity: AlertSeverity.Error);
        var historyEntry = new AlertHistoryEntry { Id = 2 };

        var existingIncident = new AlertIncident
        {
            Id = 10,
            CorrelationKey = "device:192.0.2.1",
            AlertCount = 3,
            Severity = AlertSeverity.Warning,
            LastTriggeredAt = DateTime.UtcNow.AddMinutes(-5) // Within 30min window
        };

        _repositoryMock
            .Setup(r => r.GetActiveIncidentByKeyAsync("device:192.0.2.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingIncident);

        var incident = await _service.CorrelateAsync(evt, historyEntry, _repositoryMock.Object);

        incident.Should().NotBeNull();
        incident!.Id.Should().Be(10);
        incident.AlertCount.Should().Be(4); // Incremented
        incident.Severity.Should().Be(AlertSeverity.Error); // Escalated
        historyEntry.IncidentId.Should().Be(10);

        _repositoryMock.Verify(r => r.UpdateIncidentAsync(existingIncident, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CorrelateAsync_ExistingIncidentOutsideWindow_CreatesNew()
    {
        var evt = CreateTestEvent();
        var historyEntry = new AlertHistoryEntry { Id = 3 };

        var oldIncident = new AlertIncident
        {
            Id = 10,
            CorrelationKey = "device:192.0.2.1",
            AlertCount = 5,
            LastTriggeredAt = DateTime.UtcNow.AddMinutes(-60) // Outside 30min window
        };

        _repositoryMock
            .Setup(r => r.GetActiveIncidentByKeyAsync("device:192.0.2.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldIncident);

        _repositoryMock
            .Setup(r => r.SaveIncidentAsync(It.IsAny<AlertIncident>(), It.IsAny<CancellationToken>()))
            .Callback<AlertIncident, CancellationToken>((i, _) => i.Id = 11)
            .ReturnsAsync(11);

        var incident = await _service.CorrelateAsync(evt, historyEntry, _repositoryMock.Object);

        incident.Should().NotBeNull();
        incident!.AlertCount.Should().Be(1); // New incident
        historyEntry.IncidentId.Should().Be(11);

        _repositoryMock.Verify(r => r.SaveIncidentAsync(It.IsAny<AlertIncident>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CorrelateAsync_ExistingIncident_DoesNotDowngradeSeverity()
    {
        var evt = CreateTestEvent(severity: AlertSeverity.Info);
        var historyEntry = new AlertHistoryEntry { Id = 4 };

        var existingIncident = new AlertIncident
        {
            Id = 10,
            CorrelationKey = "device:192.0.2.1",
            AlertCount = 2,
            Severity = AlertSeverity.Critical,
            LastTriggeredAt = DateTime.UtcNow.AddMinutes(-1)
        };

        _repositoryMock
            .Setup(r => r.GetActiveIncidentByKeyAsync("device:192.0.2.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingIncident);

        var incident = await _service.CorrelateAsync(evt, historyEntry, _repositoryMock.Object);

        incident.Should().NotBeNull();
        incident!.Severity.Should().Be(AlertSeverity.Critical); // Should NOT be downgraded
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task CorrelateAsync_RepositoryThrows_ReturnsNull()
    {
        var evt = CreateTestEvent();
        var historyEntry = new AlertHistoryEntry { Id = 5 };

        _repositoryMock
            .Setup(r => r.GetActiveIncidentByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var incident = await _service.CorrelateAsync(evt, historyEntry, _repositoryMock.Object);

        incident.Should().BeNull(); // Gracefully handled
    }

    #endregion
}
