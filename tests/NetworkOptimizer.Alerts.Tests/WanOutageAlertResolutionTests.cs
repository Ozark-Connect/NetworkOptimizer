using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

/// <summary>
/// The open/close half of the WAN outage alert family: which open alerts an incoming
/// monitoring.wan_* event closes, and what that does to the incident they belonged to.
/// </summary>
public class WanOutageAlertResolutionTests
{
    private readonly AlertProcessingService _service;
    private readonly Mock<IAlertRepository> _repository = new();
    private readonly List<(string[] EventTypes, string DeviceId)> _resolveCalls = [];

    public WanOutageAlertResolutionTests()
    {
        _repository
            .Setup(r => r.ResolveActiveAlertsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, string, CancellationToken>(
                (types, deviceId, _) => _resolveCalls.Add((types.ToArray(), deviceId)))
            .ReturnsAsync(new List<AlertHistoryEntry>());

        var configuration = new Mock<IConfiguration>();
        configuration.Setup(c => c["HOST_NAME"]).Returns("host.example");

        var cooldownTracker = new AlertCooldownTracker();
        _service = new AlertProcessingService(
            NullLogger<AlertProcessingService>.Instance,
            Mock.Of<IAlertEventBus>(),
            Mock.Of<IServiceScopeFactory>(),
            new AlertRuleEvaluator(cooldownTracker, NullLogger<AlertRuleEvaluator>.Instance),
            new AlertCorrelationService(NullLogger<AlertCorrelationService>.Instance),
            [],
            cooldownTracker,
            Mock.Of<IAlertSiteNameResolver>(),
            configuration.Object);
    }

    private static AlertEvent CreateEvent(string eventType, string? deviceId) => new()
    {
        EventType = eventType,
        Severity = AlertSeverity.Critical,
        Source = "monitoring",
        Title = "Test alert",
        DeviceId = deviceId
    };

    private void SetupResolved(string eventType, string deviceId, params AlertHistoryEntry[] resolved)
    {
        _repository
            .Setup(r => r.ResolveActiveAlertsAsync(
                It.Is<IReadOnlyCollection<string>>(t => t.Contains(eventType)),
                deviceId,
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, string, CancellationToken>(
                (types, device, _) => _resolveCalls.Add((types.ToArray(), device)))
            .ReturnsAsync(resolved.ToList());
    }

    #region GetWanAlertsToResolve

    [Fact]
    public void GetWanAlertsToResolve_TotalOutage_SupersedesThePartialOnTheSameWan()
    {
        var targets = AlertProcessingService.GetWanAlertsToResolve("monitoring.wan_outage", "wan2");

        targets.Should().ContainSingle();
        targets[0].DeviceId.Should().Be("wan2");
        targets[0].EventTypes.Should().Equal("monitoring.wan_outage_partial");
    }

    /// <summary>
    /// The rollup says the whole site is down, which is the same outage every per-WAN alert was
    /// describing a piece of - so it closes them all, whatever WAN they name.
    /// </summary>
    [Fact]
    public void GetWanAlertsToResolve_SiteRollupOutage_ClosesEveryPerWanAlert()
    {
        var targets = AlertProcessingService.GetWanAlertsToResolve("monitoring.wan_outage", "all-wans");

        targets.Should().ContainSingle();
        targets[0].DeviceId.Should().BeNull("a null device id means every device");
        targets[0].EventTypes.Should().Equal("monitoring.wan_outage", "monitoring.wan_outage_partial");
    }

    [Fact]
    public void GetWanAlertsToResolve_OutageWithoutDeviceId_ClosesNothing()
    {
        AlertProcessingService.GetWanAlertsToResolve("monitoring.wan_outage", null).Should().BeEmpty();
        AlertProcessingService.GetWanAlertsToResolve("monitoring.wan_outage", "").Should().BeEmpty();
    }

    [Fact]
    public void GetWanAlertsToResolve_Recovery_ClosesBothKindsOnTheWanAndTheRollup()
    {
        var targets = AlertProcessingService.GetWanAlertsToResolve("monitoring.wan_recovered", "wan");

        targets.Should().HaveCount(2);
        targets[0].DeviceId.Should().Be("wan");
        targets[0].EventTypes.Should().BeEquivalentTo(new[] { "monitoring.wan_outage", "monitoring.wan_outage_partial" });
        targets[1].DeviceId.Should().Be("all-wans");
        targets[1].EventTypes.Should().Equal("monitoring.wan_outage");
    }

    [Fact]
    public void GetWanAlertsToResolve_RecoveryWithoutDeviceId_StillClosesTheRollup()
    {
        var targets = AlertProcessingService.GetWanAlertsToResolve("monitoring.wan_recovered", null);

        targets.Should().ContainSingle();
        targets[0].DeviceId.Should().Be("all-wans");
        targets[0].EventTypes.Should().Equal("monitoring.wan_outage");
    }

    [Theory]
    [InlineData("monitoring.target_offline")]
    [InlineData("monitoring.target_recovered")]
    [InlineData("device.offline")]
    public void GetWanAlertsToResolve_EventOutsideTheWanFamily_ClosesNothing(string eventType)
    {
        AlertProcessingService.GetWanAlertsToResolve(eventType, "wan").Should().BeEmpty();
    }

    #endregion

    #region ResolveSupersededWanAlertsAsync

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_TotalOutage_ResolvesOnlyThatWansPartial()
    {
        var evt = CreateEvent("monitoring.wan_outage", "wan2");

        await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        _resolveCalls.Should().ContainSingle();
        _resolveCalls[0].DeviceId.Should().Be("wan2");
        _resolveCalls[0].EventTypes.Should().Equal("monitoring.wan_outage_partial");
    }

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_Recovery_ResolvesOutagePartialAndRollup()
    {
        var evt = CreateEvent("monitoring.wan_recovered", "wan2");

        await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        _resolveCalls.Should().HaveCount(2);
        _resolveCalls.Select(c => c.DeviceId).Should().Equal("wan2", "all-wans");
        _resolveCalls[0].EventTypes.Should().BeEquivalentTo(new[] { "monitoring.wan_outage", "monitoring.wan_outage_partial" });
        _resolveCalls[1].EventTypes.Should().Equal("monitoring.wan_outage");
    }

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_NeverTouchesAnotherWansAlerts()
    {
        var evt = CreateEvent("monitoring.wan_recovered", "wan2");

        await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        // Only this WAN and the site rollup - "wan" and "wan3" keep their open alerts, and the
        // repository handed in is already pinned to the event's site, so other sites are untouched.
        _resolveCalls.Select(c => c.DeviceId).Should().NotContain("wan").And.NotContain("wan3");
    }

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_EventOutsideTheWanFamily_ResolvesNothing()
    {
        var evt = CreateEvent("monitoring.target_offline", "wan2");

        await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        _resolveCalls.Should().BeEmpty();
        _repository.Verify(r => r.ResolveActiveAlertsAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_ResolvedAlertInIncident_RecalculatesIncidentStatus()
    {
        var resolved = new AlertHistoryEntry
        {
            Id = 5,
            EventType = "monitoring.wan_outage_partial",
            DeviceId = "wan2",
            IncidentId = 7,
            Status = AlertStatus.Resolved
        };
        SetupResolved("monitoring.wan_outage_partial", "wan2", resolved);

        var incident = new AlertIncident { Id = 7, Status = AlertStatus.Active };
        _repository.Setup(r => r.GetIncidentAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(incident);
        _repository.Setup(r => r.GetAlertsByIncidentIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlertHistoryEntry> { resolved });

        var evt = CreateEvent("monitoring.wan_outage", "wan2");
        await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        incident.Status.Should().Be(AlertStatus.Resolved);
        incident.ResolvedAt.Should().NotBeNull();
        _repository.Verify(r => r.UpdateIncidentAsync(incident, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_IncidentStillHasActiveAlerts_LeavesIncidentOpen()
    {
        var resolved = new AlertHistoryEntry
        {
            Id = 5,
            EventType = "monitoring.wan_outage_partial",
            DeviceId = "wan2",
            IncidentId = 7,
            Status = AlertStatus.Resolved
        };
        SetupResolved("monitoring.wan_outage_partial", "wan2", resolved);

        var incident = new AlertIncident { Id = 7, Status = AlertStatus.Active };
        _repository.Setup(r => r.GetIncidentAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(incident);
        _repository.Setup(r => r.GetAlertsByIncidentIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AlertHistoryEntry> { resolved, new() { Id = 6, Status = AlertStatus.Active } });

        var evt = CreateEvent("monitoring.wan_outage", "wan2");
        await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        incident.Status.Should().Be(AlertStatus.Active);
        _repository.Verify(r => r.UpdateIncidentAsync(It.IsAny<AlertIncident>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveSupersededWanAlertsAsync_RepositoryThrows_DoesNotPropagate()
    {
        _repository
            .Setup(r => r.ResolveActiveAlertsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var evt = CreateEvent("monitoring.wan_recovered", "wan2");

        var act = async () => await _service.ResolveSupersededWanAlertsAsync(evt, _repository.Object, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    #endregion
}
