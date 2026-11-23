using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// The console link speed queries skip the gateway switch chip's series: before the chip had its own fields
/// the agent wrote its speed as link_speed_mbps under an ethN name that is another port on some gateways.
/// </summary>
public class LinkSpeedQueryTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddDays(1);

    [Fact]
    public void MaxLinkSpeed_SkipsChipSeries() =>
        MonitoringInfluxClient.MaxLinkSpeedFlux("b", "aa:bb:cc:dd:ee:ff", "eth0", From, To)
            .Should().Contain(MonitoringInfluxClient.NotChipSeries.Trim());

    [Fact]
    public void LinkSpeedWindows_SkipsChipSeries() =>
        MonitoringInfluxClient.LinkSpeedWindowsFlux("b", "aa:bb:cc:dd:ee:ff", "eth0", From, To, TimeSpan.FromMinutes(5))
            .Should().Contain(MonitoringInfluxClient.NotChipSeries.Trim());

    [Fact]
    public void InterfaceRatesRaw_SkipsChipSeries() =>
        MonitoringInfluxClient.InterfaceRatesRawFlux("b", "aa:bb:cc:dd:ee:ff", From, To)
            .Should().Contain(MonitoringInfluxClient.NotChipSeries.Trim());

    [Fact]
    public void ChipFilter_KeepsSeriesWithNoPortId() =>
        MonitoringInfluxClient.NotChipSeries.Should().Contain("not exists r.port_id");
}