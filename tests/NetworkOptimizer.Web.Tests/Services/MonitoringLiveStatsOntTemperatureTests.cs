using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>An attached ONT's temperature is shown as a second reading only when it differs from the module's DDM.</summary>
public class MonitoringLiveStatsOntTemperatureTests
{
    private const string Gateway = "aa:bb:cc:dd:ee:01";
    private const string Port = "eth7";
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static MonitoringLiveStats Stats() => new(
        NullLogger<MonitoringLiveStats>.Instance,
        Mock.Of<IDbContextFactory<NetworkOptimizerDbContext>>());

    private static SfpLiveStats Record(double? ddmC, double? ontC)
    {
        var stats = Stats();
        stats.RecordSfp(Gateway, Port, -20.5, 2.1, 12.0, ddmC, 3.3, T0);
        stats.RecordSfpPon(Gateway, Port, "operation", null, null, null, null, null, T0, ontTemperatureC: ontC);
        return stats.GetSfpStats(Gateway, Port)!;
    }

    [Fact]
    public void Stick_reporting_one_sensor_twice_shows_one_temperature()
    {
        var live = Record(ddmC: 48.12, ontC: 48.12);

        live.DistinctOntTemperatureC.Should().BeNull();
        live.OntTemperatureC.Should().Be(48.12, "the raw reading is kept for the time series");
    }

    [Fact]
    public void Readings_equal_at_display_precision_collapse()
    {
        // DDM resolves 1/256 °C; the web UI rounds to two decimals.
        Record(ddmC: 48.1171875, ontC: 48.12).DistinctOntTemperatureC.Should().BeNull();
    }

    [Fact]
    public void Two_sensors_reading_differently_show_both()
    {
        Record(ddmC: 48.12, ontC: 55.0).DistinctOntTemperatureC.Should().Be(55.0);
    }

    [Fact]
    public void Ont_temperature_without_ddm_temperature_is_shown()
    {
        Record(ddmC: null, ontC: 48.12).DistinctOntTemperatureC.Should().Be(48.12);
    }

    [Fact]
    public void No_ont_temperature_shows_nothing()
    {
        Record(ddmC: 48.12, ontC: null).DistinctOntTemperatureC.Should().BeNull();
    }
}
