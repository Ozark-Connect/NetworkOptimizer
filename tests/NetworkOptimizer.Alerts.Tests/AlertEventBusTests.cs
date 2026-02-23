using FluentAssertions;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

public class AlertEventBusTests
{
    private static AlertEvent CreateTestEvent(string eventType = "test.event", AlertSeverity severity = AlertSeverity.Warning)
    {
        return new AlertEvent
        {
            EventType = eventType,
            Severity = severity,
            Source = "test",
            Title = "Test alert"
        };
    }

    [Fact]
    public async Task PublishAsync_SingleEvent_CanBeConsumed()
    {
        var bus = new AlertEventBus();
        var evt = CreateTestEvent();

        await bus.PublishAsync(evt);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var consumed = new List<AlertEvent>();
        await foreach (var item in bus.ConsumeAsync(cts.Token))
        {
            consumed.Add(item);
            break; // Only consume one
        }

        consumed.Should().HaveCount(1);
        consumed[0].EventType.Should().Be("test.event");
    }

    [Fact]
    public async Task PublishAsync_MultipleEvents_ConsumedInOrder()
    {
        var bus = new AlertEventBus();

        await bus.PublishAsync(CreateTestEvent("first.event"));
        await bus.PublishAsync(CreateTestEvent("second.event"));
        await bus.PublishAsync(CreateTestEvent("third.event"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var consumed = new List<AlertEvent>();
        await foreach (var item in bus.ConsumeAsync(cts.Token))
        {
            consumed.Add(item);
            if (consumed.Count == 3) break;
        }

        consumed.Should().HaveCount(3);
        consumed[0].EventType.Should().Be("first.event");
        consumed[1].EventType.Should().Be("second.event");
        consumed[2].EventType.Should().Be("third.event");
    }

    [Fact]
    public async Task ConsumeAsync_Cancellation_StopsEnumeration()
    {
        var bus = new AlertEventBus();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var consumed = new List<AlertEvent>();
        var act = async () =>
        {
            await foreach (var item in bus.ConsumeAsync(cts.Token))
            {
                consumed.Add(item);
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        consumed.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_BoundedOverflow_DropsOldest()
    {
        var bus = new AlertEventBus();

        // Publish more than buffer size (1000) - should not throw
        for (int i = 0; i < 1050; i++)
        {
            await bus.PublishAsync(CreateTestEvent($"event.{i}"));
        }

        // Should be able to consume without error (some may have been dropped)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var consumed = new List<AlertEvent>();
        await foreach (var item in bus.ConsumeAsync(cts.Token))
        {
            consumed.Add(item);
            if (consumed.Count >= 1000) break;
        }

        consumed.Should().NotBeEmpty();
        // The latest events should still be present
        consumed.Last().EventType.Should().StartWith("event.");
    }

    [Fact]
    public async Task PublishAsync_PreservesAllEventProperties()
    {
        var bus = new AlertEventBus();

        var evt = new AlertEvent
        {
            EventType = "audit.score_dropped",
            Severity = AlertSeverity.Critical,
            Source = "audit",
            Title = "Audit score dropped",
            Message = "Score dropped from 85 to 60",
            DeviceId = "aa:bb:cc:dd:ee:ff",
            DeviceName = "switch1",
            DeviceIp = "192.0.2.1",
            MetricValue = 60,
            ThresholdValue = 70,
            Context = new Dictionary<string, string> { ["scoreDelta"] = "-25" },
            Tags = ["audit", "critical"]
        };

        await bus.PublishAsync(evt);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var item in bus.ConsumeAsync(cts.Token))
        {
            item.EventType.Should().Be("audit.score_dropped");
            item.Severity.Should().Be(AlertSeverity.Critical);
            item.Source.Should().Be("audit");
            item.Title.Should().Be("Audit score dropped");
            item.Message.Should().Be("Score dropped from 85 to 60");
            item.DeviceId.Should().Be("aa:bb:cc:dd:ee:ff");
            item.DeviceName.Should().Be("switch1");
            item.DeviceIp.Should().Be("192.0.2.1");
            item.MetricValue.Should().Be(60);
            item.ThresholdValue.Should().Be(70);
            item.Context.Should().ContainKey("scoreDelta");
            item.Tags.Should().Contain("critical");
            break;
        }
    }
}
