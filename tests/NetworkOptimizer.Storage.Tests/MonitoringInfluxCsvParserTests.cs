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

    // The streamed query path feeds the parser one line at a time, exactly as the InfluxDB client's
    // line reader delivers them (line terminators already stripped). These pin that the line-fed
    // path produces identical results to the whole-string parse it replaced as the query-time path.

    [Fact]
    public void Line_fed_latency_parser_matches_string_parse()
    {
        var parser = new MonitoringInfluxClient.LatencyDetailCsvParser();
        foreach (var line in Csv.Split("\r\n"))
            parser.ProcessLine(line);
        var streamed = parser.Finish();

        var buffered = MonitoringInfluxClient.ParseLatencyDetailCsv(Csv);
        streamed.Keys.Should().BeEquivalentTo(buffered.Keys);
        foreach (var key in buffered.Keys)
            streamed[key].Should().Equal(buffered[key]);
    }

    [Fact]
    public void Line_fed_wan_rates_parser_matches_string_parse()
    {
        var parser = new MonitoringInfluxClient.WanRatesCsvParser();
        foreach (var line in RatesCsv.Split("\r\n"))
            parser.ProcessLine(line);

        parser.Finish().Should().Equal(MonitoringInfluxClient.ParseWanRatesCsv(RatesCsv));
    }
}
