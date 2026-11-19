using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// Provider for the "Network Optimizer Custom" PON stats JSON contract
/// (docs/features/netopt-custom-pon-contract.md): a plain HTTP endpoint, typically
/// served from the gateway or the ONT itself, returning ITU-T PON-layer stats
/// (PLOAM state, GTC status/counters, GEM/allocation counters, bridge-port
/// discards, host-link counters) as JSON. Anyone can implement the contract for
/// their ONT hardware; the first implementation gathers Lantiq `onu` CLI output
/// from a GPON SFP stick.
///
/// Usable standalone like any ONT provider (PON state and error counters flow to
/// the ont measurement), but designed to be attached to a monitored SFP module
/// (ISfpSupplementalOntProvider), where its metrics merge into that module's sfp
/// measurement on the gateway SFP poll cycle.
/// </summary>
public class NetOptCustomPonOntProvider : ISfpSupplementalOntProvider
{
    private readonly ILogger<NetOptCustomPonOntProvider> _logger;

    /// <summary>
    /// Reference endpoints are one-shot listeners (a netcat accept loop) that serve a
    /// single connection at a time and re-gather stats per request, so requests to the
    /// same endpoint must never overlap. Gated per site and endpoint, so distinct
    /// endpoints still poll in parallel.
    ///
    /// The site has to be in the key: this is a singleton shared by every site, and
    /// host:port alone is not unique across them - each site reaches its own gateway at
    /// the same name ("unifi:10420"), so two sites would serialize against each other
    /// for no reason, each waiting out the other's 15 s timeout.
    ///
    /// Not keyed on the poll context's CacheKey: two configurations on one site may point
    /// at the same endpoint, and those genuinely must not overlap.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new();

    private const int DefaultPort = 10012;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public NetOptCustomPonOntProvider(ILogger<NetOptCustomPonOntProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderKey => "netopt-custom";
    public string DisplayName => "Network Optimizer Custom (HTTP JSON)";

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        var payload = await FetchPayloadAsync(context, cancellationToken);
        if (payload == null)
            return PollResult<OntStats>.Failed(
                $"No stats could be read from {context.Host}.");
        var stats = MapToOntStats(payload);
        stats.DeviceHost = context.Host;
        stats.DeviceName = context.Name;
        return PollResult<OntStats>.Ok(stats);
    }

    public async Task<PonSupplementalStats?> PollSupplementalAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        var payload = await FetchPayloadAsync(context, cancellationToken);
        return payload == null ? null : MapToSupplemental(payload);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await FetchPayloadAsync(context, cancellationToken, throwOnError: true);
            if (payload == null)
                return (false, "Endpoint returned no parseable stats");

            var state = ToPonLinkState(payload.Ploam?.CurrState);
            var detail = state != PonLinkState.Unknown
                ? $"PLOAM {state.ToDisplayString()}"
                : "no PLOAM section";
            if (payload.GtcStatus?.OnuId != null)
                detail += $", ONU ID {payload.GtcStatus.OnuId}";
            if (payload.SfpUptimeS != null)
            {
                var up = TimeSpan.FromSeconds(payload.SfpUptimeS.Value);
                detail += $", up {(int)up.TotalDays}d {up.Hours}h {up.Minutes}m";
            }
            return (true, $"Connected - {detail}");
        }
        catch (HttpRequestException ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.Host));
        }
        catch (TaskCanceledException ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.Host));
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch and parse the endpoint. Returns null on failure (logged at Debug)
    /// unless <paramref name="throwOnError"/> (the Test button wants the message).
    /// </summary>
    private async Task<NetOptCustomPonPayload?> FetchPayloadAsync(
        OntPollContext context, CancellationToken cancellationToken, bool throwOnError = false)
    {
        var port = context.Port > 0 ? context.Port : DefaultPort;
        var url = $"http://{context.Host}:{port}/";

        var gate = _requestGates.GetOrAdd($"{context.SiteSlug}/{context.Host}:{port}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Generous timeout: reference implementations gather stats on demand
            // (e.g. an SSH round-trip into the SFP stick) before responding.
            using var client = new HttpClient(new SocketsHttpHandler { ConnectCallback = DeviceHttp.Via(context.Dialer) })
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            var json = await client.GetStringAsync(url, cancellationToken);

            var payload = ParsePayload(json);
            if (payload == null)
            {
                _logger.LogDebug("PON stats endpoint {Host} returned unparseable payload", context.Host);
                if (throwOnError) throw new InvalidOperationException("Response was not valid PON stats JSON");
                return null;
            }

            if (!string.IsNullOrEmpty(payload.Error))
            {
                _logger.LogDebug("PON stats endpoint {Host} reported error: {Error} - {Message}",
                    context.Host, payload.Error, payload.Message);
                if (throwOnError)
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(payload.Message) ? payload.Error : $"{payload.Error}: {payload.Message}");
                return null;
            }

            return payload;
        }
        catch (Exception ex) when (!throwOnError)
        {
            _logger.LogDebug(ex, "PON stats poll failed for {Host}", context.Host);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static NetOptCustomPonPayload? ParsePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetOptCustomPonPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Map the contract payload onto the shared PON supplemental DTO. Standard
    /// concepts keep their standard encodings: PLOAM states use the same strings
    /// as the ont measurement's pon_link_status; fec_errors is uncorrectable
    /// codewords; bip_errors is the raw BIP counter. DDM optics readings are a
    /// fallback only - the gateway's own SFP DDM poll takes precedence when it can
    /// read the module (see CollectSfpForDevice).
    /// </summary>
    internal static PonSupplementalStats MapToSupplemental(NetOptCustomPonPayload p) => new()
    {
        RxPowerDbm = p.Optics?.RxPowerDbm,
        TxPowerDbm = p.Optics?.TxPowerDbm,
        TemperatureC = p.Optics?.TemperatureC,
        VoltageV = p.Optics?.VoltageV,
        PloamStateRaw = p.Ploam?.CurrState,
        PonLinkStatus = EncodePloamState(p.Ploam?.CurrState),
        PonLinkStatusPrev = EncodePloamState(p.Ploam?.PreviousState),
        PloamElapsedMs = p.Ploam?.ElapsedMsec,
        GtcDsState = p.GtcStatus?.DsState,
        OnuId = p.GtcStatus?.OnuId,
        DsFecEnabled = p.GtcStatus?.DsFecEnable,
        UsFecEnabled = p.GtcStatus?.UsFecEnable,
        OnuResponseTime = p.GtcStatus?.OnuResponseTime,
        GemPorts = p.GtcStatus?.GemPorts,
        AesActive = p.Encryption?.AesActive,
        GemPortsEncrypted = p.Encryption?.GemPortsEncrypted,
        GponmgrEnc = p.Encryption?.GponmgrEnc,
        BipErrors = p.GtcCounters?.Bip,
        FecErrors = p.GtcCounters?.FecWordsUncorr,
        FecCorrectedWords = p.GtcCounters?.FecWordsCorr,
        HecCorrected = p.GtcCounters?.HecErrorCorr,
        HecUncorrected = p.GtcCounters?.HecErrorUncorr,
        BwmapCorrected = p.GtcCounters?.BwmapErrorCorr,
        BwmapUncorrected = p.GtcCounters?.BwmapErrorUncorr,
        GemTxFrames = p.GtcCounters?.TxGemFramesTotal,
        GemTxIdleFrames = p.GtcCounters?.TxGemIdleFramesTotal,
        GemRxFrames = p.GtcCounters?.RxGemFramesTotal,
        GemRxDropped = p.GtcCounters?.RxGemFramesDropped,
        AllocTotal = p.GtcCounters?.AllocationsTotal,
        AllocLost = p.GtcCounters?.AllocationsLost,
        GpePonIngressDiscard = p.GpePon?.IbpDiscard,
        GpePonEgressDiscard = p.GpePon?.EbpDiscard,
        GpePonLearningDiscard = p.GpePon?.LearningDiscard,
        GpeLanIngressDiscard = p.GpeLan?.IbpDiscard,
        GpeLanEgressDiscard = p.GpeLan?.EbpDiscard,
        GpeLanLearningDiscard = p.GpeLan?.LearningDiscard,
        LanLinkStatus = p.Lan?.LinkStatus,
        LanMode = p.Lan?.Mode,
        LanTxFrames = p.LanCounters?.TxFrames,
        LanRxFrames = p.LanCounters?.RxFrames,
        LanTxDropEvents = p.LanCounters?.TxDropEvents,
        LanRxFcsErrors = p.LanCounters?.RxFcsErr,
        LanBufferOverflow = p.LanCounters?.BufferOverflow,
        SfpUptimeS = p.SfpUptimeS,
        OltVendorId = Clean(p.OltVendorId),
        FirmwareVersion = Clean(p.SfpFwVersion),
        OltModel = Clean(p.OltModel),
        OltVersion = Clean(p.OltVersion),

        // The PON insight fields. The same PonSupplementalStats fields the odi-oss provider fills,
        // so the cards, the Connection Check and the path loss alerts read them the same way.
        FecCorrectedBits = p.GtcCounters?.FecErrorCorr,
        GemRxIdleFrames = p.GtcCounters?.RxGemIdleFramesTotal,
        DsSuperframe = p.GtcCounters?.DsSuperframe,
        UsBursts = p.GtcCounters?.UsBurstsTotal,
        UsDbru = p.GtcCounters?.UsDbruTotal,
        EqdBits = p.GtcStatus?.EqdBits is long eqd and >= 0 ? eqd : null,
        BiasMa = Finite(p.Optics?.BiasMa),
        BiasWarnHighMa = Finite(p.Optics?.BiasWarnHighMa),
        TransceiverAlarmCount = p.Optics?.Alarms?.Count,
        TransceiverAlarms = p.Optics?.Alarms is { Count: > 0 } alarms ? string.Join(",", alarms) : null,
        OltLaunchPowerDbm = Finite(p.PonId?.LaunchPowerDbm) is double launch && launch is >= -30 and <= 30 ? launch : null,
        OdnClass = p.PonId?.OdnClass is long cls and >= 0 and <= 4 ? cls : null,
        ReachExtender = p.PonId?.ReachExtender is long re and (0 or 1) ? re : null,
        PonIdAgeSeconds = Finite(p.PonId?.AgeS) is double age && age >= 0 ? age : null,
        O5Seconds = Finite(p.LinkHistory?.O5Seconds),
        NotO5Seconds = Finite(p.LinkHistory?.NotO5Seconds),
        O5Exits = p.LinkHistory?.O5Exits,
        LosEvents = p.LinkHistory?.LosEvents,
        LosSeconds = Finite(p.LinkHistory?.LosSeconds),
        LofEvents = p.LinkHistory?.LofEvents,
        LofSeconds = Finite(p.LinkHistory?.LofSeconds),
        OltDeactivations = p.PloamCounters?.Deactivations,
        PloamRequestKey = p.PloamCounters?.RequestKey,
        PloamKeySwitch = p.PloamCounters?.KeySwitch,
        PloamRangingTime = p.PloamCounters?.RangingTime,
        OmciUp = p.Omci?.Up is long omciUp and (0 or 1) ? omciUp : null,
        OmciServices = p.Omci?.Services,
        OmciRxFrames = p.Omci?.RxFrames,
        MibDataSync = p.Omci?.MibDataSync,
        TrafficPirBytesPerSecond = p.Omci?.PirBytesPerSecond,
        TrafficCirBytesPerSecond = p.Omci?.CirBytesPerSecond,
        HostLinkUp = p.Lan?.LinkUp is long up and (0 or 1) ? up : null,
        CpuBusySeconds = Finite(p.Cpu?.BusyS),
        CpuTotalSeconds = Finite(p.Cpu?.TotalS),
        BootCount = p.BootCount,
        LastResetReason = Clean(p.LastResetReason),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? Finite(double? value) => value is double v && double.IsFinite(v) ? v : null;

    /// <summary>
    /// Standalone mapping: the standard OntStats fields this contract can serve.
    /// DDM optics readings are populated only when the endpoint supplies the
    /// optional <c>optics</c> section; in the SFP-module (attached) scenario the
    /// gateway's own DDM poll owns those and takes precedence.
    /// </summary>
    internal static OntStats MapToOntStats(NetOptCustomPonPayload p) => new()
    {
        Timestamp = DateTime.UtcNow,
        RxPowerDbm = p.Optics?.RxPowerDbm,
        TxPowerDbm = p.Optics?.TxPowerDbm,
        TemperatureC = p.Optics?.TemperatureC,
        VoltageV = p.Optics?.VoltageV,
        PonLinkStatus = ToPonLinkState(p.Ploam?.CurrState),
        FecErrors = p.GtcCounters?.FecWordsUncorr,
        BipErrors = p.GtcCounters?.Bip,
        OltVendor = Clean(p.OltVendorId),
        OltModel = Clean(p.OltModel),
        // Standalone gets the same PON detail as an attached module: same endpoint, same payload,
        // so the only difference between the two modes should be which tab it lands on.
        Pon = MapToSupplemental(p),
    };

    /// <summary>Raw PLOAM state number (1-7 = O1-O7) to the shared enum.</summary>
    internal static PonLinkState ToPonLinkState(long? state) =>
        state is >= 1 and <= 7 ? (PonLinkState)(int)state.Value : PonLinkState.Unknown;

    private static string? EncodePloamState(long? state) =>
        state == null ? null : ToPonLinkState(state).ToInfluxValue();
}

/// <summary>
/// The "Network Optimizer Custom" PON stats JSON contract, v1. Every section is
/// optional - implementations serve what their hardware exposes, and absent
/// sections are simply not recorded. Counters are cumulative since ONT boot.
/// See docs/features/netopt-custom-pon-contract.md for field semantics.
/// </summary>
public class NetOptCustomPonPayload
{
    /// <summary>Machine-readable failure code (e.g. "sfp_unreachable"). Non-null means the poll failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Human-readable failure detail accompanying <see cref="Error"/>.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("optics")]
    public OpticsSection? Optics { get; set; }

    [JsonPropertyName("lan")]
    public LanSection? Lan { get; set; }

    [JsonPropertyName("lan_counters")]
    public LanCountersSection? LanCounters { get; set; }

    [JsonPropertyName("ploam")]
    public PloamSection? Ploam { get; set; }

    [JsonPropertyName("gtc_status")]
    public GtcStatusSection? GtcStatus { get; set; }

    [JsonPropertyName("gtc_counters")]
    public GtcCountersSection? GtcCounters { get; set; }

    [JsonPropertyName("gpe_pon")]
    public GpeBridgePortSection? GpePon { get; set; }

    [JsonPropertyName("gpe_lan")]
    public GpeBridgePortSection? GpeLan { get; set; }

    [JsonPropertyName("encryption")]
    public EncryptionSection? Encryption { get; set; }

    /// <summary>Seconds since the ONT module booted.</summary>
    [JsonPropertyName("sfp_uptime_s")]
    public long? SfpUptimeS { get; set; }

    /// <summary>OLT vendor ID as the ONT reports it (e.g. "CALX"). Decoded for display only.</summary>
    [JsonPropertyName("olt_vendor_id")]
    public string? OltVendorId { get; set; }

    /// <summary>The ONT module's firmware version, as the module reports it.</summary>
    [JsonPropertyName("sfp_fw_version")]
    public string? SfpFwVersion { get; set; }

    /// <summary>OLT model, from OMCI OLT-G's equipment ID (e.g. "E7").</summary>
    [JsonPropertyName("olt_model")]
    public string? OltModel { get; set; }

    /// <summary>OLT software version, from OMCI OLT-G's version attribute.</summary>
    [JsonPropertyName("olt_version")]
    public string? OltVersion { get; set; }

    /// <summary>The OLT's PON-ID PLOAM message (G.984.3 Annex C): launch power and ODN class, for Path Loss.</summary>
    [JsonPropertyName("pon_id")]
    public PonIdSection? PonId { get; set; }

    /// <summary>The ONT's own clock on its activation states and alarms, for Link Downtime.</summary>
    [JsonPropertyName("link_history")]
    public LinkHistorySection? LinkHistory { get; set; }

    /// <summary>Downstream PLOAM messages by type, for Provider Activity and Link Downtime's causes.</summary>
    [JsonPropertyName("ploam_counters")]
    public PloamCountersSection? PloamCounters { get; set; }

    /// <summary>The OLT's management of the ONT over OMCI, for Provider Activity and the Connection Check.</summary>
    [JsonPropertyName("omci")]
    public OmciSection? Omci { get; set; }

    /// <summary>The ONT's own processor time, for ONT CPU Load.</summary>
    [JsonPropertyName("cpu")]
    public CpuSection? Cpu { get; set; }

    /// <summary>Boots of the ONT since its last power cycle, this one included.</summary>
    [JsonPropertyName("boot_count")]
    public long? BootCount { get; set; }

    /// <summary>Why the ONT's previous boot ended, in the device's own word.</summary>
    [JsonPropertyName("last_reset_reason")]
    public string? LastResetReason { get; set; }

    public class PonIdSection
    {
        /// <summary>Mean launch power the OLT (or a reach extender) announces, dBm.</summary>
        [JsonPropertyName("launch_power_dbm")]
        public double? LaunchPowerDbm { get; set; }

        /// <summary>ODN class: 0 A, 1 B, 2 B+, 3 C, 4 C+.</summary>
        [JsonPropertyName("odn_class")]
        public long? OdnClass { get; set; }

        /// <summary>1 when the announced power is a reach extender's, 0 when it is the OLT's.</summary>
        [JsonPropertyName("reach_extender")]
        public long? ReachExtender { get; set; }

        /// <summary>Seconds since the last PON-ID message arrived.</summary>
        [JsonPropertyName("age_s")]
        public double? AgeS { get; set; }
    }

    public class LinkHistorySection
    {
        [JsonPropertyName("o5_seconds")]
        public double? O5Seconds { get; set; }

        [JsonPropertyName("not_o5_seconds")]
        public double? NotO5Seconds { get; set; }

        [JsonPropertyName("o5_exits")]
        public long? O5Exits { get; set; }

        [JsonPropertyName("los_events")]
        public long? LosEvents { get; set; }

        [JsonPropertyName("los_seconds")]
        public double? LosSeconds { get; set; }

        [JsonPropertyName("lof_events")]
        public long? LofEvents { get; set; }

        [JsonPropertyName("lof_seconds")]
        public double? LofSeconds { get; set; }
    }

    public class PloamCountersSection
    {
        /// <summary>Deactivate_ONU-ID and Disable_Serial_Number received, together.</summary>
        [JsonPropertyName("deactivations")]
        public long? Deactivations { get; set; }

        [JsonPropertyName("request_key")]
        public long? RequestKey { get; set; }

        [JsonPropertyName("key_switch")]
        public long? KeySwitch { get; set; }

        [JsonPropertyName("ranging_time")]
        public long? RangingTime { get; set; }
    }

    public class OmciSection
    {
        /// <summary>1 when the ONT's OMCI stack answered, 0 when it did not.</summary>
        [JsonPropertyName("up")]
        public long? Up { get; set; }

        /// <summary>Services (bridge connections) the OLT provisioned.</summary>
        [JsonPropertyName("services")]
        public long? Services { get; set; }

        [JsonPropertyName("rx_frames")]
        public long? RxFrames { get; set; }

        /// <summary>The OMCI MIB data sync counter (one byte, wraps).</summary>
        [JsonPropertyName("mib_data_sync")]
        public long? MibDataSync { get; set; }

        /// <summary>Highest peak information rate among the OLT's traffic descriptors, bytes per second.</summary>
        [JsonPropertyName("pir_bytes_per_second")]
        public long? PirBytesPerSecond { get; set; }

        /// <summary>Highest committed information rate among the OLT's traffic descriptors, bytes per second.</summary>
        [JsonPropertyName("cir_bytes_per_second")]
        public long? CirBytesPerSecond { get; set; }
    }

    public class CpuSection
    {
        /// <summary>CPU time since boot that was not idle, seconds.</summary>
        [JsonPropertyName("busy_s")]
        public double? BusyS { get; set; }

        /// <summary>CPU time since boot over all modes, seconds.</summary>
        [JsonPropertyName("total_s")]
        public double? TotalS { get; set; }
    }

    /// <summary>
    /// DDM optics readings, in the same units as SFP DDM (dBm / degrees C / volts).
    /// Optional - serve these when the module exposes DDM but the gateway cannot
    /// read it off the SFP slot (common with GPON sticks). When the config is
    /// attached to a monitored SFP module and the gateway's own DDM poll returns a
    /// value, that value wins; these are used only to fill the gaps.
    /// </summary>
    public class OpticsSection
    {
        /// <summary>Receive optical power in dBm.</summary>
        [JsonPropertyName("rx_power_dbm")]
        public double? RxPowerDbm { get; set; }

        /// <summary>Transmit optical power in dBm.</summary>
        [JsonPropertyName("tx_power_dbm")]
        public double? TxPowerDbm { get; set; }

        /// <summary>Transceiver temperature in degrees Celsius.</summary>
        [JsonPropertyName("temperature_c")]
        public double? TemperatureC { get; set; }

        /// <summary>Supply voltage in volts.</summary>
        [JsonPropertyName("voltage_v")]
        public double? VoltageV { get; set; }

        /// <summary>Laser bias current in mA, the ONT's own reading.</summary>
        [JsonPropertyName("bias_ma")]
        public double? BiasMa { get; set; }

        /// <summary>The module's own high warning limit for the bias current, mA.</summary>
        [JsonPropertyName("bias_warn_high_ma")]
        public double? BiasWarnHighMa { get; set; }

        /// <summary>The module's raised alarm and warning flags (SFF-8472), as "kind:condition" ("warning:rx_power_low"). Empty when none.</summary>
        [JsonPropertyName("alarms")]
        public List<string>? Alarms { get; set; }
    }

    public class LanSection
    {
        [JsonPropertyName("mode")]
        public long? Mode { get; set; }

        [JsonPropertyName("link_status")]
        public long? LinkStatus { get; set; }

        [JsonPropertyName("phy_duplex")]
        public long? PhyDuplex { get; set; }

        /// <summary>1 while the ONT's host port (towards the gateway) has a link, 0 when it does not.</summary>
        [JsonPropertyName("link_up")]
        public long? LinkUp { get; set; }
    }

    public class LanCountersSection
    {
        [JsonPropertyName("tx_frames")]
        public long? TxFrames { get; set; }

        [JsonPropertyName("rx_frames")]
        public long? RxFrames { get; set; }

        [JsonPropertyName("tx_drop_events")]
        public long? TxDropEvents { get; set; }

        [JsonPropertyName("rx_fcs_err")]
        public long? RxFcsErr { get; set; }

        [JsonPropertyName("buffer_overflow")]
        public long? BufferOverflow { get; set; }
    }

    public class PloamSection
    {
        /// <summary>Current ITU-T activation state, numeric: 1-7 = O1-O7.</summary>
        [JsonPropertyName("curr_state")]
        public long? CurrState { get; set; }

        [JsonPropertyName("previous_state")]
        public long? PreviousState { get; set; }

        [JsonPropertyName("elapsed_msec")]
        public long? ElapsedMsec { get; set; }
    }

    public class GtcStatusSection
    {
        [JsonPropertyName("ds_state")]
        public long? DsState { get; set; }

        [JsonPropertyName("onu_id")]
        public long? OnuId { get; set; }

        [JsonPropertyName("ds_fec_enable")]
        public long? DsFecEnable { get; set; }

        [JsonPropertyName("us_fec_enable")]
        public long? UsFecEnable { get; set; }

        [JsonPropertyName("onu_response_time")]
        public long? OnuResponseTime { get; set; }

        /// <summary>GEM Port-IDs provisioned in the GTC Port-ID table, including the OMCI port.</summary>
        [JsonPropertyName("gem_ports")]
        public long? GemPorts { get; set; }

        /// <summary>Equalization delay the OLT assigned at the last ranging, in upstream bit times.</summary>
        [JsonPropertyName("eqd_bits")]
        public long? EqdBits { get; set; }
    }

    /// <summary>Downstream AES state. Each field is null when its read failed on that poll.</summary>
    public class EncryptionSection
    {
        /// <summary>True when any GEM port has its downstream AES decrypt bit set. The AES flag.</summary>
        [JsonPropertyName("aes_active")]
        public bool? AesActive { get; set; }

        /// <summary>The enc: value of <c>dcli gponmgr show encryption</c>. Observed only on Calix EXOS so far.</summary>
        [JsonPropertyName("gponmgr_enc")]
        public long? GponmgrEnc { get; set; }

        /// <summary>GEM ports with the decrypt bit set.</summary>
        [JsonPropertyName("gem_ports_encrypted")]
        public long? GemPortsEncrypted { get; set; }
    }

    public class GtcCountersSection
    {
        [JsonPropertyName("bip")]
        public long? Bip { get; set; }

        [JsonPropertyName("hec_error_corr")]
        public long? HecErrorCorr { get; set; }

        [JsonPropertyName("hec_error_uncorr")]
        public long? HecErrorUncorr { get; set; }

        [JsonPropertyName("bwmap_error_corr")]
        public long? BwmapErrorCorr { get; set; }

        [JsonPropertyName("bwmap_error_uncorr")]
        public long? BwmapErrorUncorr { get; set; }

        [JsonPropertyName("fec_error_corr")]
        public long? FecErrorCorr { get; set; }

        [JsonPropertyName("fec_words_corr")]
        public long? FecWordsCorr { get; set; }

        [JsonPropertyName("fec_words_uncorr")]
        public long? FecWordsUncorr { get; set; }

        [JsonPropertyName("fec_words_total")]
        public long? FecWordsTotal { get; set; }

        [JsonPropertyName("fec_seconds")]
        public long? FecSeconds { get; set; }

        [JsonPropertyName("tx_gem_frames_total")]
        public long? TxGemFramesTotal { get; set; }

        [JsonPropertyName("tx_gem_bytes_total")]
        public long? TxGemBytesTotal { get; set; }

        [JsonPropertyName("tx_gem_idle_frames_total")]
        public long? TxGemIdleFramesTotal { get; set; }

        [JsonPropertyName("rx_gem_frames_total")]
        public long? RxGemFramesTotal { get; set; }

        [JsonPropertyName("rx_gem_bytes_total")]
        public long? RxGemBytesTotal { get; set; }

        [JsonPropertyName("rx_gem_frames_dropped")]
        public long? RxGemFramesDropped { get; set; }

        [JsonPropertyName("omci_drop")]
        public long? OmciDrop { get; set; }

        [JsonPropertyName("drop")]
        public long? Drop { get; set; }

        [JsonPropertyName("rx_oversized_frames")]
        public long? RxOversizedFrames { get; set; }

        [JsonPropertyName("allocations_total")]
        public long? AllocationsTotal { get; set; }

        [JsonPropertyName("allocations_lost")]
        public long? AllocationsLost { get; set; }

        /// <summary>Idle GEM frames received downstream: the PON's unused capacity, the same for every ONT on it.</summary>
        [JsonPropertyName("rx_gem_idle_frames_total")]
        public long? RxGemIdleFramesTotal { get; set; }

        /// <summary>The downstream superframe counter (one per 125 us frame; 30 bits on GPON, so it wraps), read with the counters above.</summary>
        [JsonPropertyName("ds_superframe")]
        public long? DsSuperframe { get; set; }

        /// <summary>Upstream bursts sent in the OLT's grants.</summary>
        [JsonPropertyName("us_bursts_total")]
        public long? UsBurstsTotal { get; set; }

        /// <summary>Upstream DBRu queue reports sent.</summary>
        [JsonPropertyName("us_dbru_total")]
        public long? UsDbruTotal { get; set; }
    }

    public class GpeBridgePortSection
    {
        [JsonPropertyName("ibp_good")]
        public long? IbpGood { get; set; }

        [JsonPropertyName("ibp_discard")]
        public long? IbpDiscard { get; set; }

        [JsonPropertyName("ebp_good")]
        public long? EbpGood { get; set; }

        [JsonPropertyName("ebp_discard")]
        public long? EbpDiscard { get; set; }

        [JsonPropertyName("learning_discard")]
        public long? LearningDiscard { get; set; }
    }
}
