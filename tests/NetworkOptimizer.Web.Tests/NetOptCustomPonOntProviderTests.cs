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
