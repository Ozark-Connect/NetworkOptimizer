using FluentAssertions;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Guards that every health alert event type published by <see cref="DeviceHealthAlertEvaluator"/>
/// has a corresponding extractor in <see cref="DeviceHealthChartEndpoints.AlertReading"/> and
/// a label in <see cref="DeviceHealthChartEndpoints.AlertLabel"/>. A new event type added to one
/// side without the other fails here rather than silently showing a blank subtitle or raw title
/// in the collapsed chart tooltip.
/// </summary>
public class AlertReadingArchitectureTests
{
    /// <summary>
    /// Every health alert event type constant on <see cref="DeviceHealthAlertEvaluator"/> must
    /// produce a non-null reading from a representative message.
    /// </summary>
    public static TheoryData<string, string, string> HealthAlertMessages => new()
    {
        { DeviceHealthAlertEvaluator.HighCpuEventType,
          "Gateway TestDevice CPU averaged 70.4% over the last 5 samples, exceeding the 70% threshold.",
          "70.4%" },
        { DeviceHealthAlertEvaluator.HighCpuEventType,
          "Gateway TestDevice CPU averaged 85% over the last 5 samples, exceeding the 70% threshold.",
          "85%" },
        { DeviceHealthAlertEvaluator.HighMemoryEventType,
          "Gateway TestDevice memory usage at 95.3%, exceeding the 95% threshold.",
          "95.3%" },
        { DeviceHealthAlertEvaluator.HighMemoryEventType,
          "Gateway TestDevice memory usage at 99%, exceeding the 95% threshold.",
          "99%" },
        // New format with degree symbol
        { DeviceHealthAlertEvaluator.HighTemperatureEventType,
          "Gateway TestDevice temperature at 65.3 °C, exceeding the 60 °C threshold.",
          "65.3 °C" },
        { DeviceHealthAlertEvaluator.HighTemperatureEventType,
          "Switch TestDevice temperature at 85 °C, exceeding the 85 °C threshold.",
          "85 °C" },
        // Old format without degree symbol (already in DB)
        { DeviceHealthAlertEvaluator.HighTemperatureEventType,
          "Gateway TestDevice temperature at 65.3 C, exceeding the 60 C threshold.",
          "65.3 °C" },
        { DeviceHealthAlertEvaluator.HighTemperatureEventType,
          "Switch TestDevice temperature at 85 C, exceeding the 85 C threshold.",
          "85 °C" },
    };

    [Theory]
    [MemberData(nameof(HealthAlertMessages))]
    public void AlertReading_extracts_reading_from_health_alert_message(
        string eventType, string message, string expected)
    {
        DeviceHealthChartEndpoints.AlertReading(eventType, message)
            .Should().Be(expected);
    }

    [Fact]
    public void Every_health_event_type_has_an_AlertLabel()
    {
        var eventTypes = new[]
        {
            DeviceHealthAlertEvaluator.HighCpuEventType,
            DeviceHealthAlertEvaluator.HighMemoryEventType,
            DeviceHealthAlertEvaluator.HighTemperatureEventType,
        };

        foreach (var eventType in eventTypes)
        {
            var label = DeviceHealthChartEndpoints.AlertLabel(eventType, "fallback");
            label.Should().NotBe("fallback",
                $"AlertLabel must have an entry for {eventType} - add one when adding a new health alert type");
        }
    }

    [Fact]
    public void Every_health_event_type_has_an_AlertReading_extractor()
    {
        var eventTypes = new[]
        {
            DeviceHealthAlertEvaluator.HighCpuEventType,
            DeviceHealthAlertEvaluator.HighMemoryEventType,
            DeviceHealthAlertEvaluator.HighTemperatureEventType,
        };

        foreach (var eventType in eventTypes)
        {
            // A message with a number+unit must extract; null means the event type is unhandled.
            var reading = DeviceHealthChartEndpoints.AlertReading(eventType, "Test value at 50% above 40% threshold.");
            reading.Should().NotBeNull(
                $"AlertReading must handle {eventType} - add it to the switch when adding a new health alert type");
        }
    }
}
