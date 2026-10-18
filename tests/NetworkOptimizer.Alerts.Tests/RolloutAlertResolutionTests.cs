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
/// The Firmware Rollout open/close pair: a Completed event closes the site's open Started alert,
/// and no other event touches it.
/// </summary>
public class RolloutAlertResolutionTests
{
    private readonly AlertProcessingService _service;
    private readonly Mock<IAlertRepository> _repository = new();
    private readonly List<string[]> _anyDeviceResolveCalls = [];

    public RolloutAlertResolutionTests()
    {
        _repository
            .Setup(r => r.ResolveActiveAlertsAnyDeviceAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<string>, CancellationToken>(
                (types, _) => _anyDeviceResolveCalls.Add(types.ToArray()))
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

    private static AlertEvent CreateEvent(string eventType) => new()
    {
        EventType = eventType,
        Severity = AlertSeverity.Info,
        Source = "rollout",
        Title = "Test alert",
    };

    [Fact]
    public void Completed_ClosesStartedOnAnyDevice()
    {
        var targets = AlertProcessingService.GetRolloutAlertsToResolve("rollout.completed");

        targets.Should().ContainSingle();
        targets[0].DeviceId.Should().BeNull();
        targets[0].EventTypes.Should().Equal("rollout.started");
    }

    [Theory]
    [InlineData("rollout.started")]
    [InlineData("rollout.report_ready")]
    [InlineData("rollout.device_stuck_offline")]
    [InlineData("monitoring.wan_recovered")]
    public void OtherEvents_CloseNothing(string eventType)
    {
        AlertProcessingService.GetRolloutAlertsToResolve(eventType).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveSupersededAlertsAsync_Completed_ResolvesStarted()
    {
        await _service.ResolveSupersededAlertsAsync(
            CreateEvent("rollout.completed"), _repository.Object, CancellationToken.None);

        _anyDeviceResolveCalls.Should().ContainSingle();
        _anyDeviceResolveCalls[0].Should().Equal("rollout.started");
    }
}
