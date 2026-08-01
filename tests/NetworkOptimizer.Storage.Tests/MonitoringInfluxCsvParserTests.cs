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
    // Annotated CSV exactly as Flux returns the UN-PIVOTED latency-detail query: one row per
    // (target_id, _field, _time), fields spread across separate tables and arriving out of time order.
    // The parser must reassemble them onto one point per (target, timestamp), map by column name (the
    // _time/_value/_field/target_id columns can sit anywhere), and sort by time.
    private const string Csv =
        "#group,false,false,true,true,false,false,true,true,true,true,false\r\n" +
        "#datatype,string,long,dateTime:RFC3339,dateTime:RFC3339,dateTime:RFC3339,double,string,string,string,string,string\r\n" +
        "#default,_result,,,,,,,,,,\r\n" +
        ",result,table,_start,_stop,_time,_value,_field,_measurement,target_id,target_type,vantage_point\r\n" +
        ",,0,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T01:00:00Z,0.7,jitter_ms,latency,transit-x,transit,server\r\n" +
        ",,0,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,0.3,jitter_ms,latency,transit-x,transit,server\r\n" +
        ",,1,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T01:00:00Z,0,loss_percent,latency,transit-x,transit,server\r\n" +
        ",,1,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,0,loss_percent,latency,transit-x,transit,server\r\n" +
        ",,2,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T01:00:00Z,3,rtt_avg_ms,latency,transit-x,transit,server\r\n" +
        ",,2,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,2.8,rtt_avg_ms,latency,transit-x,transit,server\r\n" +
        ",,3,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T01:00:00Z,3.4,rtt_max_ms,latency,transit-x,transit,server\r\n" +
        ",,3,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,3.1,rtt_max_ms,latency,transit-x,transit,server\r\n" +
        ",,4,2026-06-19T00:00:00Z,2026-06-19T02:00:00Z,2026-06-19T00:45:00Z,12.3,rtt_avg_ms,latency,cdn-y,internetservice,server\r\n";

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

    // The WAN rate pivot, with rate_out_bps before rate_in_bps to prove name-based mapping, and a
    // second table whose header omits rate_out_bps - which is exactly what pivot emits for an
    // interval where only one direction reported, and why the header is re-read after each #-block.
    private const string RatesCsv =
        "#group,false,false,true,true,false,false,false\r\n" +
        "#datatype,string,long,dateTime:RFC3339,dateTime:RFC3339,dateTime:RFC3339,double,double\r\n" +
        "#default,_result,,,,,,\r\n" +
        ",result,table,_start,_stop,_time,rate_out_bps,rate_in_bps\r\n" +
        ",,0,2026-07-06T00:00:00Z,2026-07-07T00:00:00Z,2026-07-06T22:00:00Z,5000000,350000000\r\n" +
        ",,0,2026-07-06T00:00:00Z,2026-07-07T00:00:00Z,2026-07-06T22:00:15Z,4000000,340000000\r\n" +
        "#group,false,false,true,true,false,false\r\n" +
        "#datatype,string,long,dateTime:RFC3339,dateTime:RFC3339,dateTime:RFC3339,double\r\n" +
        "#default,_result,,,,,\r\n" +
        ",result,table,_start,_stop,_time,rate_in_bps\r\n" +
        ",,1,2026-07-06T00:00:00Z,2026-07-07T00:00:00Z,2026-07-06T22:00:30Z,120000000\r\n";

    [Fact]
    public void Parses_wan_rates_mapping_columns_by_name_across_tables()
    {
        var result = MonitoringInfluxClient.ParseWanRatesCsv(RatesCsv);

        result.Should().HaveCount(3);
        result[0].Time.Should().Be(new DateTime(2026, 7, 6, 22, 0, 0, DateTimeKind.Utc));
        result[0].Time.Kind.Should().Be(DateTimeKind.Utc);
        result[0].DownloadBps.Should().Be(350_000_000);
        result[0].UploadBps.Should().Be(5_000_000);

        // Second table: upload column absent entirely, so upload is null rather than misread from
        // whatever sat in that position in the previous header.
        result[2].DownloadBps.Should().Be(120_000_000);
        result[2].UploadBps.Should().BeNull();
    }

    [Fact]
    public void Wan_rates_empty_or_null_csv_returns_empty()
    {
        MonitoringInfluxClient.ParseWanRatesCsv("").Should().BeEmpty();
        MonitoringInfluxClient.ParseWanRatesCsv(null!).Should().BeEmpty();
    }

    [Fact]
    public void Wan_rates_tolerate_lf_only_line_endings()
    {
        MonitoringInfluxClient.ParseWanRatesCsv(RatesCsv.Replace("\r\n", "\n"))
            .Should().HaveCount(3);
    }
}
