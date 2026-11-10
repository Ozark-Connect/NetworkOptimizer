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

    /// <summary>
    /// Idle GEM frames received downstream. Every ONT on a PON sees the same downstream, so
    /// this is the unused capacity of the whole PON, not of this ONT.
    /// </summary>
    public long? GemRxIdleFrames { get; set; }

    /// <summary>
    /// The OLT's downstream superframe counter, one count per 125 us frame. It is the time base
    /// the idle count is read against, and it wraps (30 bits on GPON).
    /// </summary>
    public long? DsSuperframe { get; set; }

    /// <summary>
    /// The same superframe counter, as it stood at the sample the cumulative counters come from.
    /// <see cref="DsSuperframe"/> is read live, up to one counter sample later than
    /// <see cref="GemRxIdleFrames"/>; this one pairs with it exactly. Null where the source does not
    /// serve it, and the live counter stands in.
    /// </summary>
    public long? DsSuperframeSampled { get; set; }

    /// <summary>
    /// Bytes received downstream on this ONT's own GEM ports, all ports together. This ONT's
    /// traffic, as opposed to <see cref="GemRxIdleFrames"/>, which describes the whole PON.
    /// </summary>
    public long? GemRxBytes { get; set; }

    /// <summary>
    /// The mean launch power the OLT announces in its PON-ID PLOAM message (G.984.3 Annex C),
    /// in dBm. A reach extender's when <see cref="ReachExtender"/> is 1. Null when the OLT sends
    /// no PON-ID or does not report the power.
    /// </summary>
    public double? OltLaunchPowerDbm { get; set; }

    /// <summary>The ODN class from the same PON-ID message: 0 A, 1 B, 2 B+, 3 C, 4 C+.</summary>
    public long? OdnClass { get; set; }

    /// <summary>1 when the announced launch power is a reach extender's, 0 when it is the OLT's.</summary>
    public long? ReachExtender { get; set; }

    /// <summary>
    /// Optical path loss in dB: what the far end says it launches, less what this ONT receives.
    /// With a reach extender in the path that is the stretch from the extender, not from the OLT.
    /// Null without both readings, and null when the result is negative, since no passive path
    /// delivers more than was launched and the two readings then do not belong together.
    /// </summary>
    public static double? PathLossDb(double? launchPowerDbm, double? rxPowerDbm)
    {
        if (launchPowerDbm is not double launch || rxPowerDbm is not double rx
            || !double.IsFinite(launch) || !double.IsFinite(rx))
            return null;
        var loss = launch - rx;
        return loss >= 0 ? Math.Round(loss, 2) : null;
    }

    /// <summary>Bytes sent upstream in GEM frames, headers included.</summary>
    public long? GemTxBytes { get; set; }

    /// <summary>Upstream bursts sent: the transmit windows the OLT granted and the ONT used.</summary>
    public long? UsBursts { get; set; }

    /// <summary>Upstream DBRu reports sent: the ONT telling the OLT how much it has queued.</summary>
    public long? UsDbru { get; set; }

    /// <summary>Seconds spent in O5 (operation) since the ONT booted, as the ONT itself timed them.</summary>
    public double? O5Seconds { get; set; }

    /// <summary>Seconds spent in any other activation state since boot: the PON link being down.</summary>
    public double? NotO5Seconds { get; set; }

    /// <summary>Times the ONT left O5 since boot. Counts drops a poll every few minutes never sees.</summary>
    public long? O5Exits { get; set; }

    /// <summary>Loss-of-signal alarms raised since boot: the light went away.</summary>
    public long? LosEvents { get; set; }

    /// <summary>Seconds the loss-of-signal alarm stood since boot.</summary>
    public double? LosSeconds { get; set; }

    /// <summary>Loss-of-frame alarms raised since boot: light, but no frame to lock onto.</summary>
    public long? LofEvents { get; set; }

    /// <summary>Seconds the loss-of-frame alarm stood since boot.</summary>
    public double? LofSeconds { get; set; }

    /// <summary>
    /// Downstream PLOAM messages by which the OLT takes an ONU off the PON, since boot:
    /// Deactivate_ONU-ID and Disable_Serial_Number together.
    /// </summary>
    public long? OltDeactivations { get; set; }

    /// <summary>Boots of the ONT since its last power cycle, this one included.</summary>
    public long? BootCount { get; set; }

    /// <summary>Why the ONT's previous boot ended, in the device's own word (reboot, power, panic, ...).</summary>
    public string? LastResetReason { get; set; }

    /// <summary>Laser bias current in mA, the ONT's own reading. Rises over the years as the laser ages.</summary>
    public double? BiasMa { get; set; }

    /// <summary>The module's own high warning limit for the bias current, in mA.</summary>
    public double? BiasWarnHighMa { get; set; }

    /// <summary>How many of the module's own alarm and warning flags are raised (SFF-8472).</summary>
    public long? TransceiverAlarmCount { get; set; }

    /// <summary>The raised module flags by name, comma separated ("warning:rx_power_low").</summary>
    public string? TransceiverAlarms { get; set; }

    /// <summary>Downstream bits the FEC decoder corrected. Cumulative; with the frame count, the bit error ratio before FEC.</summary>
    public long? FecCorrectedBits { get; set; }

    /// <summary>Downstream OMCI messages received: the OLT managing this ONT.</summary>
    public long? OmciRxFrames { get; set; }

    /// <summary>The OMCI MIB data sync counter. Moves whenever the OLT creates, sets or deletes something on the ONT (one byte, wraps).</summary>
    public long? MibDataSync { get; set; }

    /// <summary>1 when the ONT's OMCI daemon answered, 0 when it did not.</summary>
    public long? OmciUp { get; set; }

    /// <summary>Services (bridge connections) the OLT has provisioned on the ONT. 0 in O5 is registered but without a service.</summary>
    public long? OmciServices { get; set; }

    /// <summary>Request_Key PLOAM messages received: the OLT asking for a new encryption key.</summary>
    public long? PloamRequestKey { get; set; }

    /// <summary>Key_Switching_Time PLOAM messages received: the OLT switching to a new key.</summary>
    public long? PloamKeySwitch { get; set; }

    /// <summary>Ranging_Time PLOAM messages received: the OLT setting or adjusting the equalization delay.</summary>
    public long? PloamRangingTime { get; set; }

    /// <summary>Highest peak information rate among the traffic descriptors the OLT created, bytes per second.</summary>
    public long? TrafficPirBytesPerSecond { get; set; }

    /// <summary>Highest committed information rate among the traffic descriptors the OLT created, bytes per second.</summary>
    public long? TrafficCirBytesPerSecond { get; set; }

    /// <summary>1 while the ONT's host port (towards the gateway) has a link.</summary>
    public long? HostLinkUp { get; set; }

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

    /// <summary>OLT vendor ID the ONT reports (e.g. "ALCL"), raw. Decoded for display only.</summary>
    public string? OltVendorId { get; set; }

    /// <summary>ONT registration with the OLT, as the device words it (e.g. "Registered").</summary>
    public string? RegistrationStatus { get; set; }

    /// <summary>Encryption key exchange with the OLT, as the device words it (e.g. "success").</summary>
    public string? KeyExchangeStatus { get; set; }

    /// <summary>The ONT's firmware version, as the device reports it.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>OLT model (OMCI OLT-G equipment ID, e.g. "E7"), as the ONT reports it.</summary>
    public string? OltModel { get; set; }

    /// <summary>OLT software version (OMCI OLT-G version), as the ONT reports it.</summary>
    public string? OltVersion { get; set; }
}
