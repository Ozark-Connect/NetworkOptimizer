namespace NetworkOptimizer.Core.Models;

/// <summary>
/// Supplemental PON-layer statistics for an SFP ONT module, fetched from a
/// "Network Optimizer Custom (HTTP JSON)" stats endpoint and merged into the
/// module's sfp measurement during the gateway SFP poll. All counters are
/// cumulative since ONT boot; <see cref="SfpUptimeS"/> detects counter resets.
/// PLOAM states are pre-encoded with the same stable strings the ont
/// measurement's pon_link_status field uses (PonLinkStateExtensions.ToInfluxValue).
/// </summary>
public class PonSupplementalStats
{
    /// <summary>
    /// Optional DDM optics readings supplied by the endpoint, in SFP DDM units
    /// (dBm / degrees C / volts). These are a fallback: when the config is attached
    /// to a monitored SFP module and the gateway's own DDM poll reads the module,
    /// the gateway value wins and these fill only the gaps it leaves.
    /// </summary>
    public double? RxPowerDbm { get; set; }

    /// <summary>Transmit optical power in dBm. Fallback; see <see cref="RxPowerDbm"/>.</summary>
    public double? TxPowerDbm { get; set; }

    /// <summary>Transceiver temperature in degrees Celsius. Fallback; see <see cref="RxPowerDbm"/>.</summary>
    public double? TemperatureC { get; set; }

    /// <summary>Supply voltage in volts. Fallback; see <see cref="RxPowerDbm"/>.</summary>
    public double? VoltageV { get; set; }

    /// <summary>Raw ITU-T PLOAM state number (1-7 = O1-O7) for alert evaluation.</summary>
    public long? PloamStateRaw { get; set; }

    /// <summary>Current PLOAM state, encoded like the ont measurement's pon_link_status ("operation", "popup", ...).</summary>
    public string? PonLinkStatus { get; set; }

    /// <summary>Previous PLOAM state, same encoding as <see cref="PonLinkStatus"/>.</summary>
    public string? PonLinkStatusPrev { get; set; }

    /// <summary>Milliseconds spent in the current PLOAM state. uint32 on-device; wraps at ~49.7 days.</summary>
    public long? PloamElapsedMs { get; set; }

    /// <summary>Downstream GTC synchronization state (raw device enum).</summary>
    public long? GtcDsState { get; set; }

    /// <summary>ONU ID assigned by the OLT; changes on re-ranging.</summary>
    public long? OnuId { get; set; }

    /// <summary>Whether downstream FEC is enabled by the OLT profile (0/1).</summary>
    public long? DsFecEnabled { get; set; }

    /// <summary>Whether upstream FEC is enabled by the OLT profile (0/1).</summary>
    public long? UsFecEnabled { get; set; }

    /// <summary>Raw ranging response time reported by the device; changes on re-ranging.</summary>
    public long? OnuResponseTime { get; set; }

    /// <summary>BIP (bit-interleaved parity) errors. Cumulative; 0 on a healthy link.</summary>
    public long? BipErrors { get; set; }

    /// <summary>Uncorrectable FEC codewords - the data-loss signal, matching the ont measurement's fec_errors.</summary>
    public long? FecErrors { get; set; }

    /// <summary>Corrected FEC codewords - benign, early-warning counterpart to <see cref="FecErrors"/>.</summary>
    public long? FecCorrectedWords { get; set; }

    /// <summary>GTC header errors, corrected.</summary>
    public long? HecCorrected { get; set; }

    /// <summary>GTC header errors, uncorrectable.</summary>
    public long? HecUncorrected { get; set; }

    /// <summary>Upstream bandwidth-map errors, corrected.</summary>
    public long? BwmapCorrected { get; set; }

    /// <summary>Upstream bandwidth-map errors, uncorrectable.</summary>
    public long? BwmapUncorrected { get; set; }

    /// <summary>GEM frames transmitted upstream (data).</summary>
    public long? GemTxFrames { get; set; }

    /// <summary>Idle GEM frames transmitted upstream - granted capacity that went unused.</summary>
    public long? GemTxIdleFrames { get; set; }

    /// <summary>GEM frames received downstream.</summary>
    public long? GemRxFrames { get; set; }

    /// <summary>GEM frames dropped at reassembly.</summary>
    public long? GemRxDropped { get; set; }

    /// <summary>Upstream bandwidth allocations (grants) received from the OLT.</summary>
    public long? AllocTotal { get; set; }

    /// <summary>Upstream allocations lost - missed grants; scheduling/resync indicator.</summary>
    public long? AllocLost { get; set; }

    /// <summary>PON-side bridge port: ingress frames discarded.</summary>
    public long? GpePonIngressDiscard { get; set; }

    /// <summary>PON-side bridge port: egress frames discarded.</summary>
    public long? GpePonEgressDiscard { get; set; }

    /// <summary>PON-side bridge port: frames discarded by MAC learning limits.</summary>
    public long? GpePonLearningDiscard { get; set; }

    /// <summary>Host-side bridge port: ingress frames discarded.</summary>
    public long? GpeLanIngressDiscard { get; set; }

    /// <summary>Host-side bridge port: egress frames discarded.</summary>
    public long? GpeLanEgressDiscard { get; set; }

    /// <summary>Host-side bridge port: frames discarded by MAC learning limits.</summary>
    public long? GpeLanLearningDiscard { get; set; }

    /// <summary>Host (module-to-gateway) link PHY status, raw device enum.</summary>
    public long? LanLinkStatus { get; set; }

    /// <summary>Host link PHY mode, raw device enum. Tracked because a module that renegotiates
    /// its host link to a lower rate halves WAN capacity while the PON side stays healthy.</summary>
    public long? LanMode { get; set; }

    /// <summary>Frames transmitted on the host link.</summary>
    public long? LanTxFrames { get; set; }

    /// <summary>Frames received on the host link.</summary>
    public long? LanRxFrames { get; set; }

    /// <summary>Transmit drop events on the host link.</summary>
    public long? LanTxDropEvents { get; set; }

    /// <summary>FCS (checksum) errors received on the host link - signal-integrity indicator.</summary>
    public long? LanRxFcsErrors { get; set; }

    /// <summary>Buffer overflows on the host link.</summary>
    public long? LanBufferOverflow { get; set; }

    /// <summary>Seconds since the ONT module booted. Anchors counter-reset detection.</summary>
    public long? SfpUptimeS { get; set; }
}
