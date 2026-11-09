using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class OdiOssOntProviderTests
{
    // Excerpt of a live metricsd v1.8.0 scrape from a Luleey LL-XS2510 on odi-oss
    // v1.16.0-beta.1, 155 s after boot, with HELP/TYPE lines kept for a few series.
    // Only series the provider reads plus neighbours that must not be confused with them.
    private const string Scrape = """
    # HELP gpon_exporter_up Always 1. Confirms the exporter ran.
    # TYPE gpon_exporter_up gauge
    gpon_exporter_up 1
    gpon_image_info{image="v1.16.0-beta.1",built="2026-11-08T20:52Z",kernel="6.18.58",exporter="v1.8.0",confd="v1.13.0"} 1
    gpon_uptime_seconds 155.20
    gpon_bias_current_ma 15.150000
    # HELP gpon_rx_power_dbm Received optical power, in dBm.
    # TYPE gpon_rx_power_dbm gauge
    gpon_rx_power_dbm -16.345120
    gpon_tx_power_dbm 1.801546
    gpon_temperature_celsius 52.187500
    gpon_voltage_volts 3.279500
    gpon_transceiver_up 1
    gpon_transceiver_limit{measure="rx_power",level="warning",side="low"} -28.900000
    gpon_onu_state 5
    gpon_alarm{alarm="los"} 0
    gpon_ds_bip_error_blocks_total 1
    gpon_ds_bip_error_bits_total 2
    gpon_ds_fec_codewords_total{result="corrected"} 7
    gpon_ds_fec_codewords_total{result="uncorrectable"} 0
    gpon_ds_bwmap_allocations_total 2021429
    gpon_ds_gem_frames_total{kind="idle"} 7782349680
    gpon_ds_gem_frames_total{kind="non_idle"} 173982
    gpon_ds_gem_hec_corrected_total 31
    gpon_us_gem_frames_total 2021421
    gpon_fec_active{direction="ds"} 1
    gpon_fec_active{direction="us"} 0
    gpon_onu_id 4
    gpon_gem_frames_total{direction="ds",gem_port="132"} 28739
    """;

    private static PrometheusText Metrics(string text = Scrape) => PrometheusText.Parse(text);

    // --- Mapping ---------------------------------------------------------------

    [Fact]
    public void MapToOntStats_LiveScrape_MapsOpticsAndState()
    {
        var stats = OdiOssOntProvider.MapToOntStats(Metrics());

        stats.RxPowerDbm.Should().BeApproximately(-16.34512, 1e-6);
        stats.TxPowerDbm.Should().BeApproximately(1.801546, 1e-6);
        stats.TemperatureC.Should().BeApproximately(52.1875, 1e-6);
        stats.VoltageV.Should().BeApproximately(3.2795, 1e-6);
        stats.BiasMa.Should().BeApproximately(15.15, 1e-6);
        stats.PonType.Should().Be("GPON");
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.LinkState.Should().Be("Connected (O5)");
        stats.OperationalStatus.Should().Be("Up");
        stats.BipErrors.Should().Be(1);
        stats.FecErrors.Should().Be(0, "a real zero stays zero");
        stats.Pon.Should().NotBeNull();
    }

    [Fact]
    public void MapToSupplemental_LiveScrape_MapsPonCounters()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics());

        pon.PloamStateRaw.Should().Be(5);
        pon.PonLinkStatus.Should().Be("operation");
        pon.OnuId.Should().Be(4);
        pon.DsFecEnabled.Should().Be(1);
        pon.UsFecEnabled.Should().Be(0);
        pon.BipErrors.Should().Be(1, "error blocks, not bits");
        pon.FecErrors.Should().Be(0);
        pon.FecCorrectedWords.Should().Be(7);
        pon.HecCorrected.Should().Be(31);
        pon.GemTxFrames.Should().Be(2021421);
        pon.GemRxFrames.Should().Be(173982, "non-idle frames, never the idle count");
        pon.AllocTotal.Should().Be(2021429);
        pon.SfpUptimeS.Should().Be(155);
        pon.FirmwareVersion.Should().Be("v1.16.0-beta.1");
        pon.RxPowerDbm.Should().BeApproximately(-16.34512, 1e-6);
    }

    [Fact]
    public void MapToSupplemental_UnsupportedFields_StayNull()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics());

        pon.PonLinkStatusPrev.Should().BeNull();
        pon.PloamElapsedMs.Should().BeNull();
        pon.HecUncorrected.Should().BeNull();
        pon.AllocLost.Should().BeNull();
        pon.GemTxIdleFrames.Should().BeNull();
        pon.LanLinkStatus.Should().BeNull();
        pon.LanMode.Should().BeNull();
        pon.LanRxFcsErrors.Should().BeNull();
        pon.OltVendorId.Should().BeNull();
    }

    [Fact]
    public void Map_TransceiverReadFailed_LeavesOpticsAbsent()
    {
        // The exporter omits the five readings when the module's I2C read fails.
        var stats = OdiOssOntProvider.MapToOntStats(Metrics("""
        gpon_exporter_up 1
        gpon_transceiver_up 0
        gpon_onu_state 5
        """));

        stats.RxPowerDbm.Should().BeNull();
        stats.TxPowerDbm.Should().BeNull();
        stats.TemperatureC.Should().BeNull();
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
    }

    [Theory]
    [InlineData("1", PonLinkState.Initial, "Down")]
    [InlineData("6", PonLinkState.Popup, "Down")]
    [InlineData("0", PonLinkState.Unknown, null)]
    [InlineData("8", PonLinkState.Unknown, null)]
    public void Map_OnuState_MapsToLinkState(string value, PonLinkState expected, string? operational)
    {
        var stats = OdiOssOntProvider.MapToOntStats(Metrics($"gpon_exporter_up 1\ngpon_onu_state {value}\n"));

        stats.PonLinkStatus.Should().Be(expected);
        stats.OperationalStatus.Should().Be(operational);
        if (expected == PonLinkState.Unknown)
        {
            stats.LinkState.Should().BeNull();
            stats.Pon!.PloamStateRaw.Should().BeNull();
            stats.Pon.PonLinkStatus.Should().BeNull();
        }
    }

    [Fact]
    public void Map_StockFirmwareExporter_HasOpticsButNoPonCounters()
    {
        // metricsd also runs on the vendor firmware; it then has no /proc/odi_gpon.
        var stats = OdiOssOntProvider.MapToOntStats(Metrics("""
        gpon_exporter_up 1
        gpon_rx_power_dbm -20.5
        gpon_onu_state 5
        """));

        stats.RxPowerDbm.Should().Be(-20.5);
        stats.BipErrors.Should().BeNull();
        stats.Pon!.OnuId.Should().BeNull();
        stats.Pon.AllocTotal.Should().BeNull();
    }

    [Fact]
    public void IsOdiExporter_RequiresExporterUp()
    {
        OdiOssOntProvider.IsOdiExporter(Metrics()).Should().BeTrue();
        OdiOssOntProvider.IsOdiExporter(Metrics("node_load1 0.3\n")).Should().BeFalse();
        OdiOssOntProvider.IsOdiExporter(Metrics("<html><body>login</body></html>")).Should().BeFalse();
        OdiOssOntProvider.IsOdiExporter(Metrics("")).Should().BeFalse();
    }

    [Fact]
    public void Describe_LiveScrape_NamesImageStateAndUptime()
    {
        OdiOssOntProvider.Describe(Metrics())
            .Should().Be("odi-oss v1.16.0-beta.1, Connected (O5), ONU ID 4, up 0d 0h 2m");
    }

    // --- Text format -------------------------------------------------------------

    [Fact]
    public void Parse_LargeIntegerCounter_IsExact()
    {
        var m = Metrics("big_total 9007199254740993\n");

        m.Long("big_total").Should().Be(9007199254740993L, "past 2^53 a double would round");
    }

    [Fact]
    public void Parse_CounterAboveInt64_IsOmittedNotWrapped()
    {
        var m = Metrics("huge_total 18446744073709551615\n");

        m.Long("huge_total").Should().BeNull();
        m.Double("huge_total").Should().BeApproximately(1.8446744073709552e19, 1e5);
    }

    [Fact]
    public void Parse_LabelEscapesAndTimestamp_AreHandled()
    {
        var m = Metrics("info{a=\"x\\\"y\",b=\"back\\\\slash\",c=\"line\\nbreak\"} 1 1700000000000\n");

        m.Label("info", "a").Should().Be("x\"y");
        m.Label("info", "b").Should().Be("back\\slash");
        m.Label("info", "c").Should().Be("line\nbreak");
        m.Long("info").Should().Be(1, "the trailing timestamp is not the value");
    }

    [Fact]
    public void Parse_SpecialFloats_AreReadButNotMapped()
    {
        var m = Metrics("gpon_exporter_up 1\ngpon_rx_power_dbm -Inf\ngpon_tx_power_dbm NaN\n");

        m.Double("gpon_rx_power_dbm").Should().Be(double.NegativeInfinity);
        var stats = OdiOssOntProvider.MapToOntStats(m);
        stats.RxPowerDbm.Should().BeNull();
        stats.TxPowerDbm.Should().BeNull();
    }

    [Fact]
    public void Parse_MalformedLines_AreSkippedIndividually()
    {
        var m = Metrics("""
        broken{a="unterminated 1
        novalue
        {a="b"} 3
        gpon_onu_state 5
        x{a="1"}2
        """);

        m.Long("gpon_onu_state").Should().Be(5);
        m.Has("broken").Should().BeFalse();
        m.Has("novalue").Should().BeFalse();
        m.Has("x").Should().BeFalse("a value needs whitespace after the labels");
    }

    [Fact]
    public void Parse_CrLfLineEndings_AreAccepted()
    {
        var m = Metrics("gpon_exporter_up 1\r\ngpon_onu_id 4\r\n");

        m.Long("gpon_onu_id").Should().Be(4);
    }

    [Fact]
    public void Find_MatchesOnSubsetOfLabels()
    {
        var m = Metrics("""
        s{direction="ds",gem_port="4095"} 0
        s{direction="ds",gem_port="132"} 9
        """);

        m.Long("s", ("gem_port", "132")).Should().Be(9);
        m.Long("s", ("direction", "ds")).Should().Be(0, "the first match in file order wins");
        m.Long("s", ("direction", "us")).Should().BeNull();
    }
}
