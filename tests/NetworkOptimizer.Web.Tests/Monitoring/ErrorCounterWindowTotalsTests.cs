using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>How the Dashboard's 24-hour error counts reduce InfluxDB history for the ONT and cable modem cards.</summary>
public class ErrorCounterWindowTotalsTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MonitoringInfluxClient.CmPoint Cm(int minute, long? corr, long? uncorr) =>
        new() { Time = T0.AddMinutes(minute), CorrDelta = corr, UncorrDelta = uncorr };

    [Fact]
    public void Cm_SumsThePerPollDeltas()
    {
        var totals = ErrorCounterWindowService.CmTotals(new[] { Cm(0, 10, 0), Cm(5, 0, 0), Cm(10, 1_500, 3) });

        totals.Should().Be(new CmErrorTotals(1_510, 3));
    }

    [Fact]
    public void Cm_QuietDayIsZeroNotMissing()
    {
        ErrorCounterWindowService.CmTotals(new[] { Cm(0, 0, 0), Cm(5, 0, 0) })
            .Should().Be(new CmErrorTotals(0, 0));
    }

    [Fact]
    public void Cm_RowsWithOnlyGaugesCountAsNoHistory()
    {
        // The query unions gauge and delta rows; gauge-only rows carry no delta.
        ErrorCounterWindowService.CmTotals(new[] { Cm(0, null, null) }).Should().BeNull();
        ErrorCounterWindowService.CmTotals(Array.Empty<MonitoringInfluxClient.CmPoint>()).Should().BeNull();
    }

    [Fact]
    public void Cm_MissingSideCountsAsZero()
    {
        ErrorCounterWindowService.CmTotals(new[] { Cm(0, 7, null), Cm(5, null, 2) })
            .Should().Be(new CmErrorTotals(7, 2));
    }

    [Fact]
    public void Pon_TotalsEachCounterAndTakesTheLatestFecState()
    {
        var totals = ErrorCounterWindowService.Totals(
            bip: new long?[] { 100, 104 },
            fec: new long?[] { 0, 0 },
            hec: new long?[] { 50, 2, 9 },
            drops: new long?[] { null, null },
            fecState: new (long?, long?)[] { (1, 1), (0, 0), (null, null) });

        totals.Should().Be(new PonErrorTotals(Bip: 4, Fec: 0, HecUncorrected: 7, GemRxDropped: null, FecEnabled: false));
    }

    [Fact]
    public void Pon_EitherDirectionOnMeansFecOn()
    {
        ErrorCounterWindowService.Totals(new long?[] { 1, 1 }, [], [], [], new (long?, long?)[] { (0, 1) })!
            .FecEnabled.Should().BeTrue();
    }

    [Fact]
    public void Pon_NoFecStateIsUnknown()
    {
        ErrorCounterWindowService.Totals(new long?[] { 1, 1 }, [], [], [], [])!
            .FecEnabled.Should().BeNull();
    }

    [Fact]
    public void Pon_NoCounterHistoryIsNullSoTheRowStaysHidden()
    {
        // A DDM-only ONT writes optical fields but no counters.
        ErrorCounterWindowService.Totals(new long?[] { null, null }, [], [], [], new (long?, long?)[] { (1, 1) })
            .Should().BeNull();
    }
}
