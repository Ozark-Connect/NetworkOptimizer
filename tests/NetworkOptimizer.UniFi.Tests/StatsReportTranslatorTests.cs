using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class StatsReportTranslatorTests
{
    private const string Site = "00000000-0000-0000-0000-000000000001";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Site_WritesBandPrefixedRadioAttrs_ForRequestedAttrsOnly()
    {
        var stats = Parse($$"""
            [{"siteId":"{{Site}}","series":[{"timestamp":1000,"duration":300000,
              "byRadioBand":[{"radioBand":"na","avgTotalCuPct":12.5,"avgInterfCuPct":3.0,"wifiTxRetryPackets":40.4,"wifiTxAttemptPackets":400.0},
                             {"radioBand":"6e","avgTotalCuPct":2.0}]}]}]
            """);

        var rows = StatsReportTranslator.ToLegacyRows(stats, StatsReportKind.Site, Site,
            ["time", "ap-na-cu_total", "ap-na-tx_retries", "ap-na-wifi_tx_attempts", "ap-6e-cu_total"]);

        var row = rows[0];
        row.GetProperty("time").GetInt64().Should().Be(1000);
        row.GetProperty("ap-na-cu_total").GetDouble().Should().Be(12.5);
        row.GetProperty("ap-na-tx_retries").GetDouble().Should().Be(40.4);
        row.GetProperty("ap-6e-cu_total").GetDouble().Should().Be(2.0);
        row.TryGetProperty("ap-na-cu_interf", out _).Should().BeFalse("it was not requested");
    }

    [Fact]
    public void Site_DropsRowsForAnotherSite()
    {
        var stats = Parse("""[{"siteId":"other","series":[{"timestamp":1000}]}]""");
        StatsReportTranslator.ToLegacyRows(stats, StatsReportKind.Site, Site, ["time"]).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Ap_UsesUnprefixedBandAttrs_AndTagsTheAp()
    {
        var stats = Parse($$"""
            [{"siteId":"{{Site}}","deviceMac":"aa:bb:cc:dd:ee:ff","series":[{"timestamp":2000,
              "byRadioBand":[{"radioBand":"ng","avgTotalCuPct":55.0,"txPackets":10.0,"rxPackets":3.0}]}]}]
            """);

        var row = StatsReportTranslator.ToLegacyRows(stats, StatsReportKind.Ap, Site,
            ["time", "ng-cu_total", "ng-tx_packets", "ng-rx_packets"])[0];

        row.GetProperty("ng-cu_total").GetDouble().Should().Be(55.0);
        row.GetProperty("ng-tx_packets").GetDouble().Should().Be(10.0);
        row.GetProperty("ng-rx_packets").GetDouble().Should().Be(3.0);
        row.GetProperty("ap").GetString().Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public void Ap_RollsFiveMinutePointsUpToTheRequestedHour()
    {
        const long hour = 3_600_000;
        var points = string.Join(",", Enumerable.Range(0, 12).Select(i =>
            $$"""{"timestamp":{{hour * 10 + i * 300_000}},"duration":300000,"byRadioBand":[{"radioBand":"na","avgTotalCuPct":{{(i < 6 ? 10 : 30)}},"wifiTxRetryPackets":2.0,"maxWifiTxRetryPct":{{i}}}]}"""));
        var stats = Parse($$"""[{"siteId":"{{Site}}","deviceMac":"aa:bb:cc:dd:ee:ff","series":[{{points}}]}]""");

        var rows = StatsReportTranslator.ToLegacyRows(stats, StatsReportKind.Ap, Site,
            ["time", "na-cu_total", "na-tx_retries"], StatsReportTranslator.Bucket("hourly"));

        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("time").GetInt64().Should().Be(hour * 10);
        rows[0].GetProperty("na-cu_total").GetDouble().Should().Be(20.0, "a duration-weighted mean");
        rows[0].GetProperty("na-tx_retries").GetDouble().Should().Be(24.0, "counts sum");
    }

    [Fact]
    public void Ap_LeavesPointsAlone_WhenAlreadyAsCoarseAsRequested()
    {
        var stats = Parse($$"""
            [{"siteId":"{{Site}}","deviceMac":"aa:bb:cc:dd:ee:ff","series":[
              {"timestamp":0,"duration":3600000,"byRadioBand":[{"radioBand":"na","avgTotalCuPct":5.0}]},
              {"timestamp":3600000,"duration":3600000,"byRadioBand":[{"radioBand":"na","avgTotalCuPct":7.0}]}]}]
            """);

        StatsReportTranslator.ToLegacyRows(stats, StatsReportKind.Ap, Site, ["time", "na-cu_total"], StatsReportTranslator.Bucket("hourly"))
            .GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void User_SwapsClientViewToApView_AndOrdersApsByTime()
    {
        var stats = Parse($$"""
            [{"siteId":"{{Site}}","clientId":"00:11:22:33:44:55","series":[{"timestamp":3000,
              "avgSignalDbm":-61.5,"avgSatisfactionScore":97.0,"wifiRxRetryPackets":5.0,"wifiRxAttemptPackets":50.0,
              "rxPackets":900.0,"txPackets":100.0,"mostCommonRadioProtocol":"ax","mostCommonRxRateTierKbps":720600,
              "byRadioBand":[{"radioBand":"na","avgSignalDbm":-61.5}],
              "byDevice":[{"deviceMac":"aa:aa:aa:aa:aa:01","durationMs":60000.0,"byRadioBand":[{"radioBand":"ng","mostCommonChannelDescriptor":"6:20"}]},
                          {"deviceMac":"aa:aa:aa:aa:aa:02","durationMs":240000.0,"byRadioBand":[{"radioBand":"na","mostCommonChannelDescriptor":"116:80"}]}]}]}]
            """);

        var row = StatsReportTranslator.ToLegacyRows(stats, StatsReportKind.User, Site,
        [
            "time", "signal", "satisfaction", "tx_retries", "wifi_tx_attempts", "tx_packets", "rx_packets",
            "radio_protocol_most_common", "tx_rate", "na-signal", "x-set-ap_macs", "duration_map-ap_duration",
            "na-aa:aa:aa:aa:aa:02-channel_info_most_common",
        ])[0];

        row.GetProperty("signal").GetDouble().Should().Be(-61.5);
        row.GetProperty("tx_retries").GetDouble().Should().Be(5.0);
        row.GetProperty("wifi_tx_attempts").GetDouble().Should().Be(50.0);
        row.GetProperty("tx_packets").GetDouble().Should().Be(900.0, "the AP's tx is the client's rx");
        row.GetProperty("rx_packets").GetDouble().Should().Be(100.0);
        row.GetProperty("radio_protocol_most_common").GetString().Should().Be("ax");
        row.GetProperty("tx_rate").GetDouble().Should().Be(720600);
        row.GetProperty("na-signal").GetDouble().Should().Be(-61.5);
        row.GetProperty("x-set-ap_macs")[0].GetString().Should().Be("aa:aa:aa:aa:aa:02");
        row.GetProperty("duration_map-ap_duration").GetProperty("aa:aa:aa:aa:aa:01").GetDouble().Should().Be(60000);
        row.GetProperty("na-aa:aa:aa:aa:aa:02-channel_info_most_common").GetString().Should().Be("116:80");
    }
}
