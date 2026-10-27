using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

/// <summary>The notification clock is looked up only for an alert a channel takes, inside its error handling.</summary>
public class AlertDeliveryTimeZoneTests
{
    private readonly Mock<IAlertRepository> _repository = new();
    private readonly Mock<INotificationTimeZoneResolver> _timeZones = new();
    private readonly Mock<IAlertDeliveryChannel> _webhook = new();

    private AlertProcessingService Build()
    {
        _webhook.SetupGet(c => c.ChannelType).Returns(DeliveryChannelType.Webhook);
        var cooldownTracker = new AlertCooldownTracker();
        return new AlertProcessingService(
            NullLogger<AlertProcessingService>.Instance,
            Mock.Of<IAlertEventBus>(),
            Mock.Of<IServiceScopeFactory>(),
            new AlertRuleEvaluator(cooldownTracker, NullLogger<AlertRuleEvaluator>.Instance),
            new AlertCorrelationService(NullLogger<AlertCorrelationService>.Instance),
            [_webhook.Object],
            cooldownTracker,
            Mock.Of<IAlertSiteNameResolver>(),
            Mock.Of<IConfiguration>(),
            timeZones: _timeZones.Object);
    }

    private static AlertEvent Event() => new()
    {
        EventType = "test.event",
        Severity = AlertSeverity.Warning,
        Source = "test",
        Title = "Test alert",
    };

    private void Channels(params DeliveryChannel[] channels) =>
        _repository.Setup(r => r.GetEnabledChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(channels.ToList());

    [Fact]
    public async Task An_alert_no_channel_takes_costs_no_lookup()
    {
        Channels(new DeliveryChannel { Id = 1, Name = "Critical only", ChannelType = DeliveryChannelType.Webhook, MinSeverity = AlertSeverity.Critical });

        await Build().DeliverAsync(Event(), new AlertHistoryEntry(), _repository.Object, CancellationToken.None);

        _timeZones.Verify(t => t.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failed_lookup_is_recorded_as_a_delivery_error()
    {
        Channels(new DeliveryChannel { Id = 1, Name = "Ops", ChannelType = DeliveryChannelType.Webhook });
        _timeZones.Setup(t => t.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var history = new AlertHistoryEntry();

        await Build().DeliverAsync(Event(), history, _repository.Object, CancellationToken.None);

        history.DeliverySucceeded.Should().BeFalse();
        history.DeliveryError.Should().Contain("Channel 1 (Ops)");
        _repository.Verify(r => r.UpdateAlertAsync(history, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_zone_is_looked_up_once_per_alert_and_handed_to_the_channel()
    {
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        Channels(
            new DeliveryChannel { Id = 1, Name = "A", ChannelType = DeliveryChannelType.Webhook },
            new DeliveryChannel { Id = 2, Name = "B", ChannelType = DeliveryChannelType.Webhook });
        _timeZones.Setup(t => t.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(tokyo);
        _webhook.Setup(c => c.SendAsync(It.IsAny<AlertEvent>(), It.IsAny<AlertHistoryEntry>(), It.IsAny<DeliveryChannel>(),
            tokyo, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var history = new AlertHistoryEntry();

        await Build().DeliverAsync(Event(), history, _repository.Object, CancellationToken.None);

        history.DeliverySucceeded.Should().BeTrue();
        _timeZones.Verify(t => t.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
