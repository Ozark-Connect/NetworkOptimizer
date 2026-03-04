using FluentAssertions;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests.Delivery;

public class NtfyDeliveryChannelTests
{
    [Theory]
    [InlineData(AlertSeverity.Critical, 5)]
    [InlineData(AlertSeverity.Error, 4)]
    [InlineData(AlertSeverity.Warning, 3)]
    [InlineData(AlertSeverity.Info, 2)]
    public void MapPriority_ReturnsExpectedNtfyPriority(AlertSeverity severity, int expected)
    {
        NtfyDeliveryChannel.MapPriority(severity).Should().Be(expected);
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, "rotating_light")]
    [InlineData(AlertSeverity.Error, "red_circle")]
    [InlineData(AlertSeverity.Warning, "warning")]
    [InlineData(AlertSeverity.Info, "information_source")]
    public void MapTag_ReturnsExpectedEmojiShortcode(AlertSeverity severity, string expected)
    {
        NtfyDeliveryChannel.MapTag(severity).Should().Be(expected);
    }

    [Fact]
    public void MapPriority_CriticalIsHighest()
    {
        var critical = NtfyDeliveryChannel.MapPriority(AlertSeverity.Critical);
        var error = NtfyDeliveryChannel.MapPriority(AlertSeverity.Error);
        var warning = NtfyDeliveryChannel.MapPriority(AlertSeverity.Warning);
        var info = NtfyDeliveryChannel.MapPriority(AlertSeverity.Info);

        critical.Should().BeGreaterThan(error);
        error.Should().BeGreaterThan(warning);
        warning.Should().BeGreaterThan(info);
    }

    [Fact]
    public void MapPriority_AllValuesInNtfyRange()
    {
        foreach (var severity in Enum.GetValues<AlertSeverity>())
        {
            var priority = NtfyDeliveryChannel.MapPriority(severity);
            priority.Should().BeInRange(1, 5, $"ntfy priorities must be 1-5, got {priority} for {severity}");
        }
    }

    [Fact]
    public void MapTag_AllSeveritiesReturnNonEmpty()
    {
        foreach (var severity in Enum.GetValues<AlertSeverity>())
        {
            NtfyDeliveryChannel.MapTag(severity).Should().NotBeNullOrEmpty(
                $"severity {severity} should map to a tag");
        }
    }
}
