using FluentAssertions;
using NetworkOptimizer.Core.Models;
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
    gpon_ds_superframe 1223280
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
        pon.GemRxIdleFrames.Should().Be(7782349680, "the idle fill of the whole PON, kept apart");
        pon.DsSuperframe.Should().Be(1223280);
        pon.AllocTotal.Should().Be(2021429);
        pon.SfpUptimeS.Should().Be(155);
        pon.FirmwareVersion.Should().Be("v1.16.0-beta.1");
        pon.RxPowerDbm.Should().BeApproximately(-16.34512, 1e-6);
    }

    // What metricsd v1.9.0 on odi-oss v1.16.0-beta.4 adds: the PON-ID message's fields, and the
    // per-port GEM byte counters (4095 is the multicast port, idle on this line).
    private const string PonIdAndFlows = """

    gpon_olt_launch_power_dbm 3.4
    gpon_pon_id_odn_class 2
    gpon_pon_id_reach_extender 0
    gpon_gem_bytes_total{direction="ds",gem_port="4095"} 0
    gpon_gem_bytes_total{direction="ds",gem_port="132"} 1376987815
    gpon_gem_bytes_total{direction="us",gem_port="132"} 2800476136
    """;

    [Fact]
    public void MapToSupplemental_PonId_MapsLaunchPowerClassAndExtender()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + PonIdAndFlows));

        pon.OltLaunchPowerDbm.Should().Be(3.4);
        pon.OdnClass.Should().Be(2);
        pon.ReachExtender.Should().Be(0);
        PonSupplementalStats.PathLossDb(pon.OltLaunchPowerDbm, pon.RxPowerDbm).Should().BeApproximately(19.75, 0.005);
    }

    [Fact]
    public void MapToSupplemental_ReachExtender_IsCarried()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(
            Metrics(Scrape + PonIdAndFlows.Replace("gpon_pon_id_reach_extender 0", "gpon_pon_id_reach_extender 1")));

        pon.ReachExtender.Should().Be(1);
        pon.OltLaunchPowerDbm.Should().Be(3.4, "the extender's launch power, still a reading");
    }

    [Fact]
    public void MapToSupplemental_LaunchPowerNotReported_GivesNoPathLoss()
    {
        // An OLT that reports no power: the exporter drops the sample and keeps the other two.
        var pon = OdiOssOntProvider.MapToSupplemental(
            Metrics(Scrape + PonIdAndFlows.Replace("gpon_olt_launch_power_dbm 3.4", "")));

        pon.OltLaunchPowerDbm.Should().BeNull();
        pon.OdnClass.Should().Be(2);
        PonSupplementalStats.PathLossDb(pon.OltLaunchPowerDbm, pon.RxPowerDbm).Should().BeNull();
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("+Inf")]
    [InlineData("6523.5")] // the raw "not reported" code, had it been rendered
    [InlineData("-31")]
    public void MapToSupplemental_LaunchPowerNotAReading_IsNull(string value)
    {
        var pon = OdiOssOntProvider.MapToSupplemental(
            Metrics(Scrape + PonIdAndFlows.Replace("gpon_olt_launch_power_dbm 3.4", $"gpon_olt_launch_power_dbm {value}")));

        pon.OltLaunchPowerDbm.Should().BeNull();
    }

    [Fact]
    public void MapToSupplemental_PonIdOutOfRange_IsNull()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + PonIdAndFlows
            .Replace("gpon_pon_id_odn_class 2", "gpon_pon_id_odn_class 7")
            .Replace("gpon_pon_id_reach_extender 0", "gpon_pon_id_reach_extender 2")));

        pon.OdnClass.Should().BeNull();
        pon.ReachExtender.Should().BeNull();
    }

    [Fact]
    public void MapToSupplemental_NoPonIdMetrics_LeavesThemNull()
    {
        // metricsd before v1.9.0, or no PON-ID since the last loss of signal.
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics());

        pon.OltLaunchPowerDbm.Should().BeNull();
        pon.OdnClass.Should().BeNull();
        pon.ReachExtender.Should().BeNull();
        pon.DsSuperframeSampled.Should().BeNull("this exporter has no counter-sample superframe");
        pon.DsSuperframe.Should().Be(1223280, "the live counter stays, as the fallback");
    }

    [Fact]
    public void MapToSupplemental_SampledSuperframe_IsReadBesideTheLiveOne()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(
            Metrics(Scrape + $"\n{OdiOssOntProvider.SampledSuperframeMetric} 1215280\n"));

        pon.DsSuperframeSampled.Should().Be(1215280);
        pon.DsSuperframe.Should().Be(1223280);
    }

    [Fact]
    public void MapToSupplemental_OwnDownstreamBytes_SumsTheDownstreamGemPorts()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + PonIdAndFlows));

        pon.GemRxBytes.Should().Be(1376987815, "downstream ports only, the upstream counter left out");
    }

    [Fact]
    public void SumLong_NoneOrUnreadable_IsNull()
    {
        Metrics("a{d=\"ds\"} 1\na{d=\"ds\"} 2\na{d=\"us\"} 4\n").SumLong("a", ("d", "ds")).Should().Be(3);
        Metrics("a{d=\"us\"} 4\n").SumLong("a", ("d", "ds")).Should().BeNull("no downstream port");
        Metrics("a{d=\"ds\"} 1\na{d=\"ds\"} NaN\n").SumLong("a", ("d", "ds")).Should().BeNull("a partial sum is not a total");
        Metrics("a{d=\"ds\"} 9223372036854775807\na{d=\"ds\"} 1\n").SumLong("a", ("d", "ds")).Should().BeNull("overflow");
    }

    [Theory]
    [InlineData(3.4, -16.5, 19.9)]
    [InlineData(5.0, -27.25, 32.25)]
    public void PathLossDb_IsLaunchLessReceived(double launch, double rx, double expected)
    {
        PonSupplementalStats.PathLossDb(launch, rx).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void PathLossDb_MissingOrImpossible_IsNull()
    {
        PonSupplementalStats.PathLossDb(null, -16.5).Should().BeNull("no launch power");
        PonSupplementalStats.PathLossDb(3.4, null).Should().BeNull("no RX reading");
        PonSupplementalStats.PathLossDb(3.4, double.NegativeInfinity).Should().BeNull("RX below the detector floor");
        PonSupplementalStats.PathLossDb(-20, -10).Should().BeNull("more received than launched");
    }

    // The driver's state clock, alarms, PLOAM types and upstream counters, from a live scrape
    // 25 minutes after boot. A transition or a PLOAM type is printed once it has happened.
    private const string LinkHistory = """

    gpon_boot_count 6
    gpon_last_reset_reason{reason="reboot"} 1
    gpon_us_bursts_total 11685832
    gpon_us_dbru_total 11631693
    gpon_us_gem_bytes_total 1676665625
    gpon_alarm_events_total{alarm="los"} 0
    gpon_alarm_events_total{alarm="lof"} 1
    gpon_alarm_events_total{alarm="lom"} 1
    gpon_alarm_seconds_total{alarm="los"} 0.000
    gpon_alarm_seconds_total{alarm="lof"} 0.300
    gpon_onu_state_seconds_total{state="O1"} 0.000
    gpon_onu_state_seconds_total{state="O2"} 1.024
    gpon_onu_state_seconds_total{state="O3"} 1.024
    gpon_onu_state_seconds_total{state="O4"} 0.000
    gpon_onu_state_seconds_total{state="O5"} 1460.224
    gpon_onu_state_seconds_total{state="O6"} 0.000
    gpon_onu_state_seconds_total{state="O7"} 0.000
    gpon_onu_state_transitions_total{from="O1",to="O2"} 1
    gpon_onu_state_transitions_total{from="O4",to="O5"} 1
    gpon_ploam_messages_by_type_total{direction="ds",type="0x01"} 771
    gpon_ploam_messages_by_type_total{direction="us",type="0x05"} 6
    """;

    [Fact]
    public void MapToSupplemental_LinkHistory_MapsStateClockAlarmsAndUpstream()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + LinkHistory));

        pon.O5Seconds.Should().Be(1460.224);
        pon.NotO5Seconds.Should().BeApproximately(2.048, 1e-9, "every state but O5");
        pon.O5Exits.Should().Be(0, "transitions are listed and none leaves O5");
        pon.LosEvents.Should().Be(0);
        pon.LofEvents.Should().Be(1);
        pon.LofSeconds.Should().Be(0.3);
        pon.OltDeactivations.Should().Be(0, "the upstream 0x05 is another message");
        pon.BootCount.Should().Be(6);
        pon.LastResetReason.Should().Be("reboot");
        pon.UsBursts.Should().Be(11685832);
        pon.UsDbru.Should().Be(11631693);
        pon.GemTxBytes.Should().Be(1676665625);
    }

    [Fact]
    public void MapToSupplemental_DropsAndDeactivations_AreCounted()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + LinkHistory + """

        gpon_onu_state_transitions_total{from="O5",to="O6"} 2
        gpon_onu_state_transitions_total{from="O5",to="O2"} 1
        gpon_ploam_messages_by_type_total{direction="ds",type="0x05"} 1
        gpon_ploam_messages_by_type_total{direction="ds",type="0x06"} 2
        """));

        pon.O5Exits.Should().Be(3);
        pon.OltDeactivations.Should().Be(3, "Deactivate_ONU-ID and Disable_Serial_Number");
    }

    [Fact]
    public void MapToSupplemental_NoLinkHistory_LeavesItNull()
    {
        // The stock firmware's exporter, or an older metricsd: nothing to count from.
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics());

        pon.O5Seconds.Should().BeNull();
        pon.NotO5Seconds.Should().BeNull();
        pon.O5Exits.Should().BeNull("no transition family is not zero drops");
        pon.OltDeactivations.Should().BeNull();
        pon.LosEvents.Should().BeNull();
        pon.BootCount.Should().BeNull();
        pon.LastResetReason.Should().BeNull();
    }

    // Module limits and flags, OMCI state and the OLT's provisioning, from the same live scrape.
    private const string ModuleAndProvisioning = """

    gpon_transceiver_alarm{kind="alarm",condition="bias_current_high"} 0
    gpon_transceiver_alarm{kind="warning",condition="rx_power_low"} 0
    gpon_transceiver_limit{measure="bias_current",level="alarm",side="high"} 100.000000
    gpon_transceiver_limit{measure="bias_current",level="warning",side="high"} 90.000000
    gpon_transceiver_limit{measure="bias_current",level="warning",side="low"} 1.000000
    gpon_ds_fec_corrected_bits_total 12
    gpon_ds_omci_frames_total 663
    gpon_provision_mib_data_sync 160
    gpon_omci_up 1
    gpon_omci_services 3
    gpon_provision_traffic_descriptor_cir_bytes_per_second{descriptor="1"} 167772125
    gpon_provision_traffic_descriptor_pir_bytes_per_second{descriptor="1"} 167772125
    gpon_provision_traffic_descriptor_pir_bytes_per_second{descriptor="2"} 1250000
    gpon_host_link_up 1
    gpon_ploam_messages_by_type_total{direction="ds",type="0x04"} 3
    gpon_ploam_messages_by_type_total{direction="ds",type="0x0d"} 1
    gpon_ploam_messages_by_type_total{direction="ds",type="0x13"} 3
    """;

    [Fact]
    public void MapToSupplemental_ModuleAndProvisioning_AreMapped()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + ModuleAndProvisioning));

        pon.BiasMa.Should().Be(15.15);
        pon.BiasWarnHighMa.Should().Be(90, "the warning limit on the high side, not the alarm limit or the low side");
        pon.TransceiverAlarmCount.Should().Be(0);
        pon.TransceiverAlarms.Should().BeNull();
        pon.FecCorrectedBits.Should().Be(12);
        pon.OmciRxFrames.Should().Be(663);
        pon.MibDataSync.Should().Be(160);
        pon.OmciUp.Should().Be(1);
        pon.OmciServices.Should().Be(3);
        pon.TrafficPirBytesPerSecond.Should().Be(167772125, "the highest descriptor");
        pon.TrafficCirBytesPerSecond.Should().Be(167772125);
        pon.HostLinkUp.Should().Be(1);
        pon.PloamRangingTime.Should().Be(3);
        pon.PloamRequestKey.Should().Be(1);
        pon.PloamKeySwitch.Should().Be(3);
    }

    [Fact]
    public void MapToSupplemental_RaisedModuleFlags_AreNamed()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + ModuleAndProvisioning
            .Replace("condition=\"rx_power_low\"} 0", "condition=\"rx_power_low\"} 1")
            .Replace("condition=\"bias_current_high\"} 0", "condition=\"bias_current_high\"} 1")));

        pon.TransceiverAlarmCount.Should().Be(2);
        pon.TransceiverAlarms.Should().Be("alarm:bias_current_high,warning:rx_power_low");
    }

    [Fact]
    public void MapToSupplemental_OmciChannelPort_IsNotOwnTraffic()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + """

        gpon_gem_bytes_total{direction="ds",gem_port="4095"} 5000
        gpon_gem_bytes_total{direction="ds",gem_port="132"} 70000
        """));

        pon.GemRxBytes.Should().Be(70000, "GEM port 4095 is the OMCI channel");
    }

    [Fact]
    public void MapToSupplemental_NoModuleOrProvisioningMetrics_LeavesThemNull()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics());

        pon.BiasWarnHighMa.Should().BeNull();
        pon.TransceiverAlarmCount.Should().BeNull("no flags reported is not zero flags raised");
        pon.OmciUp.Should().BeNull();
        pon.OmciServices.Should().BeNull();
        pon.MibDataSync.Should().BeNull();
        pon.TrafficPirBytesPerSecond.Should().BeNull();
        pon.HostLinkUp.Should().BeNull();
        pon.PloamKeySwitch.Should().BeNull();
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

    private const string RangingAndCpu = """

        gpon_ranging_eqd_bits 283549
        gpon_pon_id_received_uptime_seconds 4080.500
        gpon_counter_sample_ds_superframe 1215280
        gpon_cpu_seconds_total{mode="user"} 77.44
        gpon_cpu_seconds_total{mode="system"} 893.09
        gpon_cpu_seconds_total{mode="idle"} 39530.95
        gpon_cpu_seconds_total{mode="softirq"} 7.74
        """;

    [Fact]
    public void MapToSupplemental_RangingAndCpu_AreReadWhenTheExporterServesThem()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics(Scrape + RangingAndCpu));

        pon.EqdBits.Should().Be(283549);
        pon.DsSuperframeSampled.Should().Be(1215280);
        pon.CpuBusySeconds.Should().BeApproximately(978.27, 0.001);
        pon.CpuTotalSeconds.Should().BeApproximately(40509.22, 0.001);
    }

    [Fact]
    public void MapToSupplemental_OlderExporter_LeavesRangingAndCpuEmpty()
    {
        var pon = OdiOssOntProvider.MapToSupplemental(Metrics());

        pon.EqdBits.Should().BeNull();
        pon.PonIdAgeSeconds.Should().BeNull();
        pon.CpuBusySeconds.Should().BeNull();
        pon.CpuTotalSeconds.Should().BeNull();
    }

    [Theory]
    [InlineData(4085.0, 4080.5, 4.5)]
    [InlineData(4080.0, 4080.5, 0.0)] // read a moment apart
    public void PonIdAge_IsTheUptimeLessTheStamp(double uptime, double stamp, double expected) =>
        OdiOssOntProvider.PonIdAge(uptime, stamp).Should().Be(expected);

    [Theory]
    [InlineData(4085.0, null)]
    [InlineData(null, 4080.5)]
    [InlineData(4085.0, 9000.0)]      // a stamp from the future is not a reading
    [InlineData(4400000.0, 105032.7)] // past 49.7 days the 32-bit stamp has wrapped
    public void PonIdAge_IsNullWhenItCannotBeTrusted(double? uptime, double? stamp) =>
        OdiOssOntProvider.PonIdAge(uptime, stamp).Should().BeNull();

}
