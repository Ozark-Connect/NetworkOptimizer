using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class NetOptCustomPonOntProviderTests
{
    // Real payload from the reference implementation (a GPON SFP stick relayed
    // through the gateway), trimmed only in counter magnitudes.
    private const string SamplePayload = """
    {
      "optics": { "rx_power_dbm": -18.4, "tx_power_dbm": 2.1, "temperature_c": 47.3, "voltage_v": 3.28 },
      "lan": { "mode": 15, "link_status": 5, "phy_duplex": 1 },
      "lan_counters": {
        "tx_frames": 671630919, "rx_frames": 850075595,
        "tx_drop_events": 0, "rx_fcs_err": 3, "buffer_overflow": 0
      },
      "ploam": { "curr_state": 5, "previous_state": 4, "elapsed_msec": 4294791528 },
      "gtc_status": {
        "ds_state": 3, "onu_id": 49, "ds_fec_enable": 0, "us_fec_enable": 0,
        "onu_response_time": 34992
      },
      "gtc_counters": {
        "bip": 7, "hec_error_corr": 0, "hec_error_uncorr": 1,
        "bwmap_error_corr": 0, "bwmap_error_uncorr": 0,
        "fec_error_corr": 0, "fec_words_corr": 12, "fec_words_uncorr": 2,
        "fec_words_total": 1000, "fec_seconds": 0,
        "tx_gem_frames_total": 848555332, "tx_gem_bytes_total": 1774937051,
        "tx_gem_idle_frames_total": 1076427353,
        "rx_gem_frames_total": 682156109, "rx_gem_bytes_total": 0,
        "rx_gem_frames_dropped": 1, "omci_drop": 0, "drop": 1,
        "rx_oversized_frames": 0,
        "allocations_total": 1629046208, "allocations_lost": 20
      },
      "gpe_pon": { "ibp_good": 670798566, "ibp_discard": 4, "ebp_good": 848843235, "ebp_discard": 0, "learning_discard": 0 },
      "gpe_lan": { "ibp_good": 848843244, "ibp_discard": 0, "ebp_good": 670798569, "ebp_discard": 5, "learning_discard": 0 },
      "sfp_uptime_s": 358825
    }
    """;

    [Fact]
    public void ParsePayload_FullSample_ParsesAllSections()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload(SamplePayload);

        p.Should().NotBeNull();
        p!.Error.Should().BeNull();
        p.Ploam!.CurrState.Should().Be(5);
        p.Ploam.PreviousState.Should().Be(4);
        p.GtcStatus!.OnuId.Should().Be(49);
        p.GtcCounters!.Bip.Should().Be(7);
        p.GtcCounters.AllocationsLost.Should().Be(20);
        p.GpePon!.IbpDiscard.Should().Be(4);
        p.GpeLan!.EbpDiscard.Should().Be(5);
        p.LanCounters!.RxFcsErr.Should().Be(3);
        p.Optics!.RxPowerDbm.Should().Be(-18.4);
        p.Optics.TxPowerDbm.Should().Be(2.1);
        p.Optics.TemperatureC.Should().Be(47.3);
        p.Optics.VoltageV.Should().Be(3.28);
        p.SfpUptimeS.Should().Be(358825);
    }

    [Fact]
    public void MapToSupplemental_UsesStandardEncodingsAndCuratedFields()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload(SamplePayload)!;
        var s = NetOptCustomPonOntProvider.MapToSupplemental(p);

        // Standard concepts keep the ont measurement's encodings/semantics.
        s.PonLinkStatus.Should().Be("operation");
        s.PonLinkStatusPrev.Should().Be("ranging");
        s.PloamStateRaw.Should().Be(5);
        s.BipErrors.Should().Be(7);
        s.FecErrors.Should().Be(2, "fec_errors is UNCORRECTABLE codewords, not corrected");
        s.FecCorrectedWords.Should().Be(12);

        s.HecUncorrected.Should().Be(1);
        s.GemTxFrames.Should().Be(848555332);
        s.GemTxIdleFrames.Should().Be(1076427353);
        s.GemRxDropped.Should().Be(1);
        s.AllocTotal.Should().Be(1629046208);
        s.AllocLost.Should().Be(20);
        s.GpePonIngressDiscard.Should().Be(4);
        s.GpeLanEgressDiscard.Should().Be(5);
        s.LanLinkStatus.Should().Be(5);
        s.LanRxFcsErrors.Should().Be(3);
        s.OnuId.Should().Be(49);
        s.OnuResponseTime.Should().Be(34992);
        s.DsFecEnabled.Should().Be(0);
        s.SfpUptimeS.Should().Be(358825);

        // Optional DDM optics carried as a fallback for gaps the gateway can't read.
        s.RxPowerDbm.Should().Be(-18.4);
        s.TxPowerDbm.Should().Be(2.1);
        s.TemperatureC.Should().Be(47.3);
        s.VoltageV.Should().Be(3.28);
    }

    [Fact]
    public void MapToOntStats_PopulatesStandardOntFields()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload(SamplePayload)!;
        var stats = NetOptCustomPonOntProvider.MapToOntStats(p);

        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.FecErrors.Should().Be(2);
        stats.BipErrors.Should().Be(7);
        stats.RxPowerDbm.Should().Be(-18.4);
        stats.TxPowerDbm.Should().Be(2.1);
        stats.TemperatureC.Should().Be(47.3);
        stats.VoltageV.Should().Be(3.28);
    }

    // Values from the odi-oss stick scrapes in OdiOssOntProviderTests (#1383): live, except eqd_bits and
    // cpu, which are that file's fixture. pon_id.age_s has no captured value, so it is a round number.
    [Fact]
    public void MapToSupplemental_PonInsightFields()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload("""
        {
          "optics": { "rx_power_dbm": -16.34512, "bias_ma": 15.15, "bias_warn_high_ma": 90, "alarms": ["warning:rx_power_low"] },
          "lan": { "link_up": 1 },
          "ploam": { "curr_state": 5 },
          "gtc_status": { "eqd_bits": 283549 },
          "gtc_counters": {
            "fec_error_corr": 12, "rx_gem_idle_frames_total": "7782349680", "ds_superframe": 1223280,
            "us_bursts_total": 11685832, "us_dbru_total": 11631693
          },
          "pon_id": { "launch_power_dbm": 3.4, "odn_class": 2, "reach_extender": 0, "age_s": 5 },
          "link_history": { "o5_seconds": 1460.224, "not_o5_seconds": 2.048, "o5_exits": 0, "los_events": 0, "los_seconds": 0, "lof_events": 1, "lof_seconds": 0.3 },
          "ploam_counters": { "deactivations": 0, "request_key": 1, "key_switch": 3, "ranging_time": 3 },
          "omci": { "up": 1, "services": 3, "rx_frames": 663, "mib_data_sync": 160, "pir_bytes_per_second": 167772125, "cir_bytes_per_second": 167772125 },
          "cpu": { "busy_s": 978.27, "total_s": 40509.22 },
          "boot_count": 6,
          "last_reset_reason": " reboot "
        }
        """)!;

        var s = NetOptCustomPonOntProvider.MapToSupplemental(p);

        s.FecCorrectedBits.Should().Be(12);
        s.GemRxIdleFrames.Should().Be(7782349680, "64-bit counters may arrive as numeric strings");
        s.DsSuperframe.Should().Be(1223280);
        s.UsBursts.Should().Be(11685832);
        s.UsDbru.Should().Be(11631693);
        s.EqdBits.Should().Be(283549);
        s.BiasMa.Should().Be(15.15);
        s.BiasWarnHighMa.Should().Be(90);
        s.TransceiverAlarmCount.Should().Be(1);
        s.TransceiverAlarms.Should().Be("warning:rx_power_low", "a raised flag, the name the exporter's family uses");
        s.HostLinkUp.Should().Be(1);
        s.OltLaunchPowerDbm.Should().Be(3.4);
        s.OdnClass.Should().Be(2);
        s.ReachExtender.Should().Be(0);
        s.PonIdAgeSeconds.Should().Be(5);
        s.O5Seconds.Should().Be(1460.224);
        s.NotO5Seconds.Should().Be(2.048);
        s.O5Exits.Should().Be(0);
        s.LosEvents.Should().Be(0);
        s.LosSeconds.Should().Be(0);
        s.LofEvents.Should().Be(1);
        s.LofSeconds.Should().Be(0.3);
        s.OltDeactivations.Should().Be(0);
        s.PloamRequestKey.Should().Be(1);
        s.PloamKeySwitch.Should().Be(3);
        s.PloamRangingTime.Should().Be(3);
        s.OmciUp.Should().Be(1);
        s.OmciServices.Should().Be(3);
        s.OmciRxFrames.Should().Be(663);
        s.MibDataSync.Should().Be(160);
        s.TrafficPirBytesPerSecond.Should().Be(167772125);
        s.TrafficCirBytesPerSecond.Should().Be(167772125);
        s.CpuBusySeconds.Should().Be(978.27);
        s.CpuTotalSeconds.Should().Be(40509.22);
        s.BootCount.Should().Be(6);
        s.LastResetReason.Should().Be("reboot");
        NetworkOptimizer.Core.Models.PonSupplementalStats.PathLossDb(s.OltLaunchPowerDbm, s.RxPowerDbm).Should().Be(19.75);
        NetOptCustomPonOntProvider.MapToOntStats(p).Pon!.GemRxIdleFrames.Should().Be(7782349680, "standalone carries the same detail");
    }

    [Fact]
    public void MapToSupplemental_OutOfRangeInsightValues_AreDropped()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload("""
        {
          "lan": { "link_up": 7 },
          "gtc_status": { "eqd_bits": -1 },
          "pon_id": { "launch_power_dbm": 99, "odn_class": 9, "reach_extender": 2, "age_s": -5 },
          "omci": { "up": 3 },
          "optics": { "alarms": [] }
        }
        """)!;

        var s = NetOptCustomPonOntProvider.MapToSupplemental(p);

        s.HostLinkUp.Should().BeNull();
        s.EqdBits.Should().BeNull();
        s.OltLaunchPowerDbm.Should().BeNull();
        s.OdnClass.Should().BeNull();
        s.ReachExtender.Should().BeNull();
        s.PonIdAgeSeconds.Should().BeNull();
        s.OmciUp.Should().BeNull();
        s.TransceiverAlarmCount.Should().Be(0, "an empty list says every flag is clear");
        s.TransceiverAlarms.Should().BeNull();
    }

    [Fact]
    public void Encryption_MapsToBothModes()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload("""
        {
          "ploam": { "curr_state": 5 },
          "gtc_status": { "onu_id": 25, "gem_ports": 3 },
          "encryption": { "aes_active": true, "gponmgr_enc": 1, "gem_ports_encrypted": 2 }
        }
        """)!;

        var s = NetOptCustomPonOntProvider.MapToSupplemental(p);
        s.GemPorts.Should().Be(3);
        s.AesActive.Should().BeTrue();
        s.GponmgrEnc.Should().Be(1);
        s.GemPortsEncrypted.Should().Be(2);
        NetOptCustomPonOntProvider.MapToOntStats(p).Pon!.AesActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "ploam": { "curr_state": 5 } }""")]
    [InlineData("""{ "ploam": { "curr_state": 5 }, "gtc_status": { "gem_ports": null }, "encryption": { "aes_active": null, "gponmgr_enc": null, "gem_ports_encrypted": null } }""")]
    public void Encryption_AbsentOrNull_IsMissingNotFalse(string json)
    {
        var s = NetOptCustomPonOntProvider.MapToSupplemental(NetOptCustomPonOntProvider.ParsePayload(json)!);

        s.GemPorts.Should().BeNull();
        s.AesActive.Should().BeNull();
        s.GponmgrEnc.Should().BeNull();
        s.GemPortsEncrypted.Should().BeNull();
    }

    [Fact]
    public void OltVendorId_MapsToBothModes_AndIsOptional()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload("""{ "ploam": { "curr_state": 5 }, "olt_vendor_id": " CALX " }""")!;

        NetOptCustomPonOntProvider.MapToSupplemental(p).OltVendorId.Should().Be("CALX");
        NetOptCustomPonOntProvider.MapToOntStats(p).OltVendor.Should().Be("CALX", "standalone, it rides the ont measurement's olt_vendor tag");

        var without = NetOptCustomPonOntProvider.ParsePayload(SamplePayload)!;
        NetOptCustomPonOntProvider.MapToSupplemental(without).OltVendorId.Should().BeNull();
        NetOptCustomPonOntProvider.MapToOntStats(without).OltVendor.Should().BeNull();
    }

    [Fact]
    public void SfpFwVersion_MapsToFirmwareVersionInBothModes_AndIsOptional()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload("""{ "ploam": { "curr_state": 5 }, "sfp_fw_version": " V1.0-220923 " }""")!;

        NetOptCustomPonOntProvider.MapToSupplemental(p).FirmwareVersion.Should().Be("V1.0-220923");
        NetOptCustomPonOntProvider.MapToOntStats(p).Pon!.FirmwareVersion.Should().Be("V1.0-220923", "standalone carries the same PON detail");

        var without = NetOptCustomPonOntProvider.ParsePayload(SamplePayload)!;
        NetOptCustomPonOntProvider.MapToSupplemental(without).FirmwareVersion.Should().BeNull();
    }

    [Fact]
    public void OltModelAndVersion_MapToBothModes_AndAreOptional()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload(
            """{ "olt_vendor_id": "CALX", "olt_model": " E7 ", "olt_version": "1.2.3" }""")!;

        var s = NetOptCustomPonOntProvider.MapToSupplemental(p);
        s.OltModel.Should().Be("E7");
        s.OltVersion.Should().Be("1.2.3");
        NetOptCustomPonOntProvider.MapToOntStats(p).OltModel.Should().Be("E7", "standalone, it rides the ont measurement's olt_model tag");

        var without = NetOptCustomPonOntProvider.MapToSupplemental(NetOptCustomPonOntProvider.ParsePayload(SamplePayload)!);
        without.OltModel.Should().BeNull();
        without.OltVersion.Should().BeNull();
    }

    [Fact]
    public void ParsePayload_ErrorShape_SurfacesErrorCode()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload(
            """{ "error": "sfp_unreachable", "message": "Could not reach SFP" }""");

        p.Should().NotBeNull();
        p!.Error.Should().Be("sfp_unreachable");
        p.Message.Should().Be("Could not reach SFP");
    }

    [Fact]
    public void ParsePayload_EmptyObject_AllSectionsNull()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload("{}");

        p.Should().NotBeNull();
        p!.Ploam.Should().BeNull();
        p.GtcCounters.Should().BeNull();
        p.Optics.Should().BeNull();
        p.SfpUptimeS.Should().BeNull();

        var s = NetOptCustomPonOntProvider.MapToSupplemental(p);
        s.PonLinkStatus.Should().BeNull();
        s.BipErrors.Should().BeNull();
        s.FecErrors.Should().BeNull();
        s.RxPowerDbm.Should().BeNull();
        s.VoltageV.Should().BeNull();
    }

    [Fact]
    public void ParsePayload_StringNumbers_AreTolerated()
    {
        var p = NetOptCustomPonOntProvider.ParsePayload(
            """{ "ploam": { "curr_state": "5" }, "gtc_status": { "onu_id": "49" } }""");

        p!.Ploam!.CurrState.Should().Be(5);
        p.GtcStatus!.OnuId.Should().Be(49);
    }

    [Fact]
    public void ParsePayload_Garbage_ReturnsNull()
    {
        NetOptCustomPonOntProvider.ParsePayload("not json at all").Should().BeNull();
        NetOptCustomPonOntProvider.ParsePayload("").Should().BeNull();
    }

    [Theory]
    [InlineData(1, PonLinkState.Initial)]
    [InlineData(4, PonLinkState.Ranging)]
    [InlineData(5, PonLinkState.Operation)]
    [InlineData(6, PonLinkState.Popup)]
    [InlineData(7, PonLinkState.EmergencyStop)]
    [InlineData(0, PonLinkState.Unknown)]
    [InlineData(8, PonLinkState.Unknown)]
    [InlineData(null, PonLinkState.Unknown)]
    public void ToPonLinkState_MapsItuStateNumbers(int? raw, PonLinkState expected)
    {
        NetOptCustomPonOntProvider.ToPonLinkState(raw).Should().Be(expected);
    }
}
