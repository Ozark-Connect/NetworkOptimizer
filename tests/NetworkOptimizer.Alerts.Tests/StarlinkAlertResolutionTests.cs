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
/// The open/close half of the Starlink dish alert family: which open alerts an incoming
/// starlink.* event closes. The promise is one open alert per (dish, condition) - a condition
/// that raises again supersedes its own open alert rather than stacking a second, a recovery
/// closes only the condition it names, and one dish's alerts never touch another's.
/// </summary>
public class StarlinkAlertResolutionTests
{
    private readonly AlertProcessingService _service;
    private readonly Mock<IAlertRepository> _repository = new();
    private readonly List<(string[] EventTypes, string DeviceId)> _resolveCalls = [];

    public StarlinkAlertResolutionTests()
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

    private static AlertEvent CreateEvent(string eventType, string? deviceId,
        Dictionary<string, string>? context = null) => new()
        {
            EventType = eventType,
            Severity = AlertSeverity.Warning,
            Source = "starlink",
            Title = "Test alert",
            DeviceId = deviceId,
            Context = context ?? new Dictionary<string, string>()
        };

    [Fact]
    public void ConditionRaisingAgain_SupersedesItsOwnOpenAlert()
    {
        var targets = AlertProcessingService.GetStarlinkAlertsToResolve(
            "starlink.obstructed", "starlink:3", null);

        targets.Should().ContainSingle();
        targets[0].DeviceId.Should().Be("starlink:3");
        targets[0].EventTypes.Should().Equal("starlink.obstructed");
    }

    /// <summary>A dish that clears its obstruction keeps any other alert it still has open.</summary>
    [Fact]
    public void Recovery_ClosesOnlyTheConditionItNames()
    {
        var targets = AlertProcessingService.GetStarlinkAlertsToResolve(
            "starlink.recovered", "starlink:3",
            new Dictionary<string, string> { ["recovered_type"] = "starlink.obstructed" });

        targets.Should().ContainSingle();
        targets[0].DeviceId.Should().Be("starlink:3");
        targets[0].EventTypes.Should().Equal("starlink.obstructed");
    }

    [Fact]
    public void RecoveryNamingNoCondition_ClosesNothing()
    {
        AlertProcessingService.GetStarlinkAlertsToResolve("starlink.recovered", "starlink:3", null)
            .Should().BeEmpty();
        AlertProcessingService.GetStarlinkAlertsToResolve("starlink.recovered", "starlink:3",
            new Dictionary<string, string> { ["recovered_type"] = "" }).Should().BeEmpty();
    }

    [Fact]
    public void EventWithoutADish_ClosesNothing()
    {
        AlertProcessingService.GetStarlinkAlertsToResolve("starlink.obstructed", null, null).Should().BeEmpty();
        AlertProcessingService.GetStarlinkAlertsToResolve("starlink.obstructed", "", null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("monitoring.wan_outage")]
    [InlineData("cellular.signal_poor")]
    [InlineData("device.offline")]
    public void EventOutsideTheStarlinkFamily_ClosesNothing(string eventType)
    {
        AlertProcessingService.GetStarlinkAlertsToResolve(eventType, "starlink:3", null).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveSupersededAlertsAsync_Recovery_ResolvesTheNamedConditionOnThatDishOnly()
    {
        var evt = CreateEvent("starlink.recovered", "starlink:3",
            new Dictionary<string, string> { ["recovered_type"] = "starlink.alignment_drift" });

        await _service.ResolveSupersededAlertsAsync(evt, _repository.Object, CancellationToken.None);

        _resolveCalls.Should().ContainSingle();
        _resolveCalls[0].DeviceId.Should().Be("starlink:3");
        _resolveCalls[0].EventTypes.Should().Equal("starlink.alignment_drift");
    }

    [Fact]
    public async Task ResolveSupersededAlertsAsync_NeverTouchesAnotherDishsAlerts()
    {
        var evt = CreateEvent("starlink.obstructed", "starlink:3");

        await _service.ResolveSupersededAlertsAsync(evt, _repository.Object, CancellationToken.None);

        _resolveCalls.Select(c => c.DeviceId).Should().Equal("starlink:3");
    }

    [Fact]
    public async Task ResolveSupersededAlertsAsync_RepositoryThrows_DoesNotPropagate()
    {
        _repository
            .Setup(r => r.ResolveActiveAlertsAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var evt = CreateEvent("starlink.recovered", "starlink:3",
            new Dictionary<string, string> { ["recovered_type"] = "starlink.obstructed" });

        var act = async () => await _service.ResolveSupersededAlertsAsync(evt, _repository.Object, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
