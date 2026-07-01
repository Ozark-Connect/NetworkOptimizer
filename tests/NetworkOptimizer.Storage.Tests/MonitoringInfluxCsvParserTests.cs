using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Verifies the span-based annotated-CSV parser that replaced the buffered FluxRecord path for the
/// high-volume latency-detail reads. A parser is all-or-nothing, so these pin the exact column
/// mapping, UTC handling, null handling, sort, and line-ending tolerance.
/// </summary>
public class MonitoringInfluxCsvParserTests
{
    // Annotated CSV exactly as Flux returns the latency-detail pivot query. The field columns are in
    // a deliberately non-positional order (jitter, loss, rtt_avg, rtt_max) to prove the parser maps
    // by column name, not index.
    private const string Csv =
        "#group,false,false,true,true,false,true,true,false,false,false,false\r\n" +
        "#datatype,string,long,dateTime:RFC3339,dateTime:RFC3339,dateTime:RFC3339,string,string,double,double,double,double\r\n" +
        "#default,_result,,,,,,,,,,\r\n" +
        ",result,table,_start,_stop,_time,target_id,target_type,jitter_ms,loss_percent,rtt_avg_ms,rtt_max_ms\r\n" +
        ",,0,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T01:00:00Z,transit-x,transit,0.7,0,3,3.4\r\n" +
        ",,0,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,transit-x,transit,0.3,0,2.8,3.1\r\n" +
        ",,1,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,cdn-y,internetservice,,,12.3,\r\n";

    [Fact]
    public void Parses_points_per_target_mapping_columns_by_name()
    {
        var result = MonitoringInfluxClient.ParseLatencyDetailCsv(Csv);

        result.Should().ContainKeys("transit-x", "cdn-y");
        result["transit-x"].Should().HaveCount(2);
        result["cdn-y"].Should().HaveCount(1);

        // Sorted by time within target, even though the 01:00 row appeared before the 00:45 row.
        var tx = result["transit-x"];
        tx[0].Time.Should().Be(new DateTime(2026, 6, 19, 0, 45, 0, DateTimeKind.Utc));
        tx[0].Time.Kind.Should().Be(DateTimeKind.Utc);
        tx[0].RttAvgMs.Should().Be(2.8);
        tx[0].RttMaxMs.Should().Be(3.1);
        tx[0].JitterMs.Should().Be(0.3);
        tx[0].LossPercent.Should().Be(0);
        tx[1].Time.Should().Be(new DateTime(2026, 6, 19, 1, 0, 0, DateTimeKind.Utc));
        tx[1].JitterMs.Should().Be(0.7);
        tx[1].RttAvgMs.Should().Be(3);
    }

    [Fact]
    public void Empty_cells_become_null()
    {
        var cdn = MonitoringInfluxClient.ParseLatencyDetailCsv(Csv)["cdn-y"][0];
        cdn.RttAvgMs.Should().Be(12.3);
        cdn.JitterMs.Should().BeNull();
        cdn.LossPercent.Should().BeNull();
        cdn.RttMaxMs.Should().BeNull();
    }

    [Fact]
    public void Empty_or_null_csv_returns_empty()
    {
        MonitoringInfluxClient.ParseLatencyDetailCsv("").Should().BeEmpty();
        MonitoringInfluxClient.ParseLatencyDetailCsv(null!).Should().BeEmpty();
    }

    [Fact]
    public void Tolerates_lf_only_line_endings()
    {
        var result = MonitoringInfluxClient.ParseLatencyDetailCsv(Csv.Replace("\r\n", "\n"));
        result["transit-x"].Should().HaveCount(2);
        result["cdn-y"][0].RttAvgMs.Should().Be(12.3);
    }
}
